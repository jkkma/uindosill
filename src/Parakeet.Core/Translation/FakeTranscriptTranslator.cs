using System.Runtime.CompilerServices;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Translation;

public sealed record FakeTranslatorOptions
{
    public static FakeTranslatorOptions Default { get; } = new();

    /// <summary>
    /// What the canned translator puts in front of every segment's own text. Visible on purpose:
    /// the fake must never be mistakable for a translation, in a test's assertion or in a file
    /// somebody opens.
    /// </summary>
    public string Prefix { get; init; } = "[en] ";

    /// <summary>Fixed delay per segment, for exercising progress and cancellation.</summary>
    public TimeSpan PerSegmentDelay { get; init; } = TimeSpan.Zero;

    /// <summary>Throw from <see cref="ITranscriptTranslator.LoadAsync"/> instead of loading.</summary>
    public bool FailOnLoad { get; init; }

    /// <summary>
    /// Load, then throw from <see cref="ITranscriptTranslator.TranslateAsync"/> on every file — the
    /// shape of a per-file failure inside the pass, which is what the callers' fallback to the
    /// untranslated transcript is exercised against.
    /// </summary>
    public bool FailOnTranslate { get; init; }

    /// <summary>
    /// The provider this translator claims to have run on, on the same terms as its sibling on the
    /// canned labeller: Cpu by default, settable so a test can tell a populated provenance field
    /// from a defaulted one.
    /// </summary>
    public ComputeBackend Backend { get; init; } = ComputeBackend.Cpu;

    /// <summary>
    /// Refuse a source longer than this many tokens, as a real translator refuses one past its
    /// encoder window. The fake has no SentencePiece model, so it counts whitespace-separated
    /// tokens and says so; what it exercises is the refusal path, not the count.
    /// </summary>
    public int? MaxSourceTokens { get; init; }

    /// <summary>
    /// What the fake says its search is, so the provenance path that carries the real translator's
    /// decode description into a transcript is exercised with no weights. Null to say nothing.
    /// </summary>
    public string? DecodeDescription { get; init; } = "canned, beam 1";

    /// <summary>
    /// Strip every digit from the translation, so the lost-numbers warning has something real to
    /// find. A translation that loses a date is the failure <c>TranslationNumerals</c> exists to
    /// flag, and an echoing fake can never produce one.
    /// </summary>
    public bool DropDigits { get; init; }

    /// <summary>
    /// The target token the fake declares: the many-to-one family's by default, so everything built
    /// on top of it is exercised against the string a real translator uses, and null to stand in
    /// for a single-direction checkpoint, whose sources go bare.
    /// </summary>
    public string? TargetToken { get; init; } = ">>eng<<";
}

/// <summary>
/// A translator that reads no model and returns marked English anyway: every segment comes back as
/// its own text behind a visible prefix, with no words, the same times, the same source index and
/// the same speaker.
/// </summary>
/// <remarks>
/// <para>
/// Mandatory rather than convenient. The whole suite runs with no weights on disk, so without a
/// canned translator nothing downstream of this seam — the request marking, the contract the driver
/// enforces, the <c>.en</c> output naming, the refusal of the word-timed format, the CLI flag —
/// is testable until an ONNX export and a decode loop exist. That is how a feature ships having
/// never been run end to end.
/// </para>
/// <para>
/// It exercises the real invariants rather than standing beside them: sources are built through
/// <see cref="TranslationRequest.Build"/>, so every one of them carries the target token and a test
/// can assert it; the context the options ask for is really assembled; word timings really are
/// dropped; and a source past <see cref="FakeTranslatorOptions.MaxSourceTokens"/> really is refused
/// rather than truncated.
/// </para>
/// </remarks>
public sealed class FakeTranscriptTranslator : ITranscriptTranslator
{
    private readonly FakeTranslatorOptions _options;
    private readonly List<TranslationRequest> _requests = [];
    private bool _loaded;

    public FakeTranscriptTranslator(FakeTranslatorOptions? options = null)
    {
        _options = options ?? FakeTranslatorOptions.Default;
        Capabilities = new TranslatorCapabilities
        {
            EngineName = "fake",
            ModelId = "fake-translator",
            Backend = _options.Backend,
            DecodeDescription = _options.DecodeDescription,

            // The token the many-to-one family reads, unless a test says otherwise. The fake
            // carries the real one so that everything built on top of it — the marking, the
            // assertions, the CLI — is exercised against the string a real translator will use
            // rather than a placeholder.
            TargetToken = _options.TargetToken,
            RequiresSourceLanguage = false,
            PreservesWordTimings = false,
            SupportsCancellation = true,
            MaxSourceTokens = _options.MaxSourceTokens,
            SourceLanguages = [],
            TargetLanguages = [TranslationTarget.LanguageTag],
        };
    }

    public int LoadCount { get; private set; }

    /// <summary>
    /// Every request the last <see cref="TranslateAsync"/> built, in order, so a test can assert
    /// that the target token reached the model and that the context option did something.
    /// </summary>
    public IReadOnlyList<TranslationRequest> Requests => _requests;

    public TranslatorCapabilities Capabilities { get; }

    public ValueTask LoadAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            return ValueTask.CompletedTask;
        }

        if (_options.FailOnLoad)
        {
            throw new InvalidOperationException("Fake translator was configured to fail on load.");
        }

        LoadCount++;
        _loaded = true;
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<TranscriptSegment> TranslateAsync(
        IReadOnlyList<TranscriptSegment> segments,
        TranslationOptions options,
        IProgress<TranscriptionProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        await LoadAsync(ct).ConfigureAwait(false);

        if (_options.FailOnTranslate)
        {
            throw new InvalidOperationException("Fake translator was configured to fail on every file.");
        }

        var requests = TranslationRequest.Build(segments, options, Capabilities.TargetToken);
        _requests.Clear();
        _requests.AddRange(requests);

        for (var i = 0; i < requests.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var request = requests[i];
            if (Capabilities.MaxSourceTokens is { } limit)
            {
                var tokens = request.Source.Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

                if (tokens > limit)
                {
                    throw new SegmentTooLongException(request.SegmentIndex, tokens, limit);
                }
            }

            if (_options.PerSegmentDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.PerSegmentDelay, ct).ConfigureAwait(false);
            }

            var segment = segments[i];
            var text = segment.Text.Trim();
            if (_options.DropDigits)
            {
                text = string.Concat(text.Where(c => !char.IsAsciiDigit(c)));
            }

            yield return segment with
            {
                // Empty in, empty out: the counts have to line up, and a prefix on nothing would
                // turn a silent segment into a line of text.
                Text = text.Length == 0 ? string.Empty : _options.Prefix + text,

                // Not the source's words with new text over them. Translation reorders and
                // rewrites, so no alignment survives, and handing back the old list would be a lie
                // with timestamps on it.
                Words = [],
            };

            progress?.Report(new TranscriptionProgress
            {
                Stage = TranscriptionStage.Translating,
                Processed = segment.End,
                Total = segments[^1].End,
                SegmentsCompleted = i + 1,
                SegmentsTotal = segments.Count,
            });
        }
    }

    public ValueTask DisposeAsync()
    {
        _loaded = false;
        return ValueTask.CompletedTask;
    }
}
