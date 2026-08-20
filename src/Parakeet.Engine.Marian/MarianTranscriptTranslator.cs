using System.Runtime.CompilerServices;
using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;

namespace Parakeet.Engine.Marian;

/// <summary>How to load the translator.</summary>
public sealed record MarianTranslatorOptions
{
    /// <summary>The exported checkpoint directory: two graphs, two configs, five tokenizer files.</summary>
    public required string ModelDirectory { get; init; }

    /// <summary>Catalogue id, carried into the transcript's provenance beside the ASR model's.</summary>
    public string? ModelId { get; init; }

    /// <summary>Intra-op threads for both ONNX sessions, or 0 to let ONNX Runtime choose.</summary>
    public int IntraOpThreads { get; init; }

    /// <summary>
    /// BCP-47 tags the catalogue says this checkpoint reads. Empty when unconstrained or unknown.
    /// </summary>
    /// <remarks>
    /// Passed in rather than read from the model, because it is a claim about a list and claims
    /// belong in the catalogue where a release engineer can review one. The checkpoint's own card
    /// disclaims its coverage list in as many words.
    /// </remarks>
    public IReadOnlyList<string> SourceLanguages { get; init; } = [];
}

/// <summary>
/// The translator that reads real weights: Marian, ONNX Runtime, CPU, beam-6.
/// </summary>
/// <remarks>
/// <para>
/// One segment at a time and one sentence per decode, which is measured rather than convenient.
/// Batching a beam search pads every member to the longest and decodes until the last one finishes:
/// on this project's CPU, sixteen Spanish sentences at a time cost 12.75 s each against 2.16 s each
/// one at a time, a factor of six the wrong way. On a GPU the same change goes the other way, which
/// is a fact about CPUs and not about this model — and v1 translation is CPU-only, priced on
/// 2026-08-20 at 1.2–1.5× for CUDA against a diariser that gains 22× on the same runtime.
/// </para>
/// <para>
/// <b>What this does not do is as decided as what it does.</b> It carries no word timings, because
/// translation reorders and rewrites and no alignment survives between the words that were spoken
/// and the words that come back. It never truncates a source: one past the tokenizer's 512 is
/// refused, because half a sentence translated fluently is the failure the contract exists to
/// avoid. And it does not read <see cref="TranslationOptions.ContextSegments"/> — see
/// <see cref="TranslatorCapabilities.HonoursContext"/>, which is false here and said out loud
/// rather than silently ignored.
/// </para>
/// </remarks>
public sealed class MarianTranscriptTranslator : ITranscriptTranslator
{
    /// <summary>
    /// The token every source must carry. One vocabulary entry, id 693, not three punctuation
    /// marks and a word.
    /// </summary>
    public const string EnglishTargetToken = ">>eng<<";

    private readonly MarianTranslatorOptions _options;
    private readonly Lock _gate = new();

    private Task<Loaded>? _load;
    private bool _disposed;

    public MarianTranscriptTranslator(MarianTranslatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelDirectory);

        _options = options;

        Capabilities = new TranslatorCapabilities
        {
            EngineName = "marian-onnx",
            ModelId = options.ModelId,

            // CPU, and that is measured rather than a limitation nobody priced. One ONNX Runtime
            // native serves this and the diariser, so neither moves onto CUDA alone.
            Backend = ComputeBackend.Cpu,
            TargetToken = EnglishTargetToken,

            // Many-to-one: it is told the target and never the source, which is what makes it
            // drivable by a pipeline that cannot detect what it just transcribed.
            RequiresSourceLanguage = false,
            PreservesWordTimings = false,

            // The search polls between steps, so a segment already being decoded really does stop.
            SupportsCancellation = true,
            HonoursContext = false,

            // Filled in from the tokenizer at load; 512 before then, which is what it will be.
            MaxSourceTokens = 512,
            SourceLanguages = options.SourceLanguages,
            TargetLanguages = [TranslationTarget.LanguageTag],
        };
    }

    public TranslatorCapabilities Capabilities { get; private set; }

    public async ValueTask LoadAsync(CancellationToken ct = default) =>
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

    public async IAsyncEnumerable<TranscriptSegment> TranslateAsync(
        IReadOnlyList<TranscriptSegment> segments,
        TranslationOptions options,
        IProgress<TranscriptionProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var loaded = await EnsureLoadedAsync(ct).ConfigureAwait(false);

        // Built here rather than inline so that no translator can forget the target token: a source
        // without it comes back as fluent German rather than as an error.
        var requests = TranslationRequest.Build(segments, options, Capabilities.TargetToken);
        var total = segments.Count > 0 ? segments[^1].End : TimeSpan.Zero;

        for (var i = 0; i < requests.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var segment = segments[i];
            var request = requests[i];
            var source = request.Source.Trim();

            string text;
            if (segment.Text.Trim().Length == 0)
            {
                // Nothing to translate. Yielded empty rather than dropped, and rather than handed
                // to the model, which given a bare target token would confidently write a sentence
                // nobody said.
                text = string.Empty;
            }
            else
            {
                var ids = loaded.Tokenizer.Encode(source);
                if (Capabilities.MaxSourceTokens is { } limit && ids.Count > limit)
                {
                    throw new SegmentTooLongException(request.SegmentIndex, ids.Count, limit);
                }

                text = await Task.Run(() => Translate(loaded, ids, ct), ct).ConfigureAwait(false);
            }

            yield return segment with
            {
                Text = text,

                // Not the source's words under new text. Every clause of the contract that costs
                // something costs it here.
                Words = [],
            };

            progress?.Report(new TranscriptionProgress
            {
                Stage = TranscriptionStage.Translating,
                Processed = segment.End,
                Total = total,
                SegmentsCompleted = i + 1,
                SegmentsTotal = segments.Count,
            });
        }
    }

    /// <summary>Translates one already-tokenised source.</summary>
    private static string Translate(Loaded loaded, IReadOnlyList<int> ids, CancellationToken ct)
    {
        var output = MarianBeamSearch.Search(
            loaded.Decoder, loaded.Configuration, ids, MarianDecodeSettings.Default, ct);

        return loaded.Tokenizer.Decode(output);
    }

    private Task<Loaded> EnsureLoadedAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        if (_load is { } existing)
        {
            return existing;
        }

        lock (_gate)
        {
            // The token is checked above and not handed to Task.Run: building two InferenceSessions
            // cannot be interrupted part-way, so passing it would only let a cancelled task be
            // cached as this translator's one and only load.
            return _load ??= Task.Run(Load);
        }
    }

    private Loaded Load()
    {
        var configuration = MarianConfiguration.Load(_options.ModelDirectory);
        var tokenizer = MarianTokenizer.Load(_options.ModelDirectory);
        var decoder = new MarianOnnxDecoder(
            _options.ModelDirectory,
            configuration,
            new MarianSessionOptions { IntraOpThreads = _options.IntraOpThreads });

        // The tokenizer's own declared limit rather than the constructor's guess. They agree on
        // this checkpoint; a checkpoint where they did not is one to hear about.
        Capabilities = Capabilities with { MaxSourceTokens = tokenizer.MaxLength };

        return new Loaded(configuration, tokenizer, decoder);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_load is { } load)
        {
            try
            {
                (await load.ConfigureAwait(false)).Decoder.Dispose();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A load that threw has no sessions to dispose, and rethrowing from Dispose would
                // replace whatever the caller was already handling with the same failure again.
            }
        }
    }

    /// <summary>Everything a decode needs, loaded once.</summary>
    internal sealed record Loaded(
        MarianConfiguration Configuration,
        MarianTokenizer Tokenizer,
        MarianOnnxDecoder Decoder);
}
