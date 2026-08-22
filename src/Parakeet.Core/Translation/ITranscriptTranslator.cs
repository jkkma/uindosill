using Parakeet.Core.Transcription;

namespace Parakeet.Core.Translation;

/// <summary>
/// The one direction this product translates. A constant rather than a setting because it is a
/// product decision and not a knob: into English is the best-resourced direction in every open
/// translation family, which makes it the direction whose quality claims are cheapest to support,
/// and this project ships no claim it cannot measure. English into anything else is out of scope,
/// which is why the flag is <c>--translate</c> against a translate-to-English pass rather than a
/// <c>translate</c> that would later have to grow a target.
/// </summary>
public static class TranslationTarget
{
    /// <summary>BCP-47 tag of the target, as the catalogue and the transcript's provenance spell it.</summary>
    public const string LanguageTag = "en";
}

/// <summary>What a caller may ask of a translation pass.</summary>
public sealed record TranslationOptions
{
    public static TranslationOptions Default { get; } = new();

    /// <summary>
    /// How many preceding segments are handed to the model as context ahead of the one being
    /// translated. Zero — the default — translates each segment on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller's only lever over surrounding context, and deliberately the only lever at all.
    /// Everything else is fixed by decision rather than offered: the target is English with no
    /// picker, because this pipeline cannot detect what it has just transcribed and the
    /// recommended many-to-one family never asks — a source-language control would be asking the
    /// user to assert something no part of the pass needs.
    /// </para>
    /// <para>
    /// Zero by default because nothing has measured what context buys. A 30-second segment
    /// measures at most about 190 tokens against a 512-token tokenizer limit, so one or two
    /// segments of context fit inside the model's window (docs/UNPROVEN.md § <i>Translating into
    /// English</i>); whether they improve the output is unmeasured, and a non-zero default would
    /// be a claim that they do.
    /// </para>
    /// </remarks>
    public int ContextSegments { get; init; }

    public void Validate()
    {
        if (ContextSegments < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ContextSegments), ContextSegments, "Context cannot be a negative number of segments.");
        }
    }
}

/// <summary>What a loaded translator can do, so a caller can refuse rather than degrade.</summary>
/// <remarks>
/// On the same terms as <c>SpeakerLabellerCapabilities</c>: a caller that hands over something the
/// translator will not honour says so out loud instead of dropping it. Here the flag that matters
/// most is <see cref="PreservesWordTimings"/>, which decides whether the word-timed subtitle format
/// can be written at all.
/// </remarks>
public sealed record TranslatorCapabilities
{
    public required string EngineName { get; init; }

    /// <summary>
    /// Identifier of the loaded translation model, carried into the transcript's provenance beside
    /// the ASR model's id and the diariser's — a transcript that cannot say which model wrote its
    /// English is not a result anybody can re-examine later.
    /// </summary>
    public string? ModelId { get; init; }

    public ComputeBackend Backend { get; init; } = ComputeBackend.Cpu;

    /// <summary>
    /// The target-language token every source string this model reads must begin with.
    /// </summary>
    /// <remarks>
    /// Not a convention a caller remembers: measured on 2026-08-19, the recommended checkpoint
    /// handed the same Spanish segments without <c>&gt;&gt;eng&lt;&lt;</c> returned fluent German —
    /// its first declared target — rather than an error. A forgotten prefix therefore produces
    /// confident output in the wrong language that no downstream check would catch, so the token
    /// is declared here and applied by <see cref="TranslationRequest.Build"/>, which is the only
    /// way a source string is built.
    /// </remarks>
    public required string TargetToken { get; init; }

    /// <summary>
    /// True when the model has to be told which language it is reading. False for the family this
    /// product ships: it is many-to-one, the source is never declared, only the target.
    /// </summary>
    /// <remarks>
    /// A translator that says true cannot be driven by this product as it stands. Nothing here can
    /// detect the source — the transcript's <c>language</c> field records the request rather than
    /// a detection and the ASR's language hint is inert on this checkpoint — so the caller refuses
    /// rather than guessing, and says which of the two it is refusing.
    /// </remarks>
    public bool RequiresSourceLanguage { get; init; }

    /// <summary>
    /// The search that produced the English, as one phrase — beam width, length cap, length
    /// penalty, early stopping — when the translator can say. Null when it cannot.
    /// </summary>
    /// <remarks>
    /// Provenance beside <see cref="ModelId"/> and <see cref="Backend"/>, because the graphs are
    /// pinned and the search over them is not: beam width alone moved this project's own measured
    /// output, so a transcript that records which checkpoint ran has recorded half of what produced
    /// its English. Until 2026-08-22 the sidecar reported this and only the <c>translate</c> verb's
    /// stderr ever showed it; no transcript carried it.
    /// </remarks>
    public string? DecodeDescription { get; init; }

    /// <summary>
    /// True when a translated segment still carries per-word timings. False on everything this
    /// product can ship, and false is a real answer rather than a placeholder.
    /// </summary>
    /// <remarks>
    /// Translation reorders and rewrites; the words that come back are not the words that were
    /// spoken and no alignment survives between them. Handing back the source's word list with new
    /// text on top would be a lie with timestamps on it, so a translator that says false yields
    /// <see cref="TranscriptSegment.Words"/> empty and callers refuse the word-timed formats rather
    /// than writing a file whose highlight lands on the wrong word.
    /// </remarks>
    public bool PreservesWordTimings { get; init; }

    /// <summary>
    /// True when a translation in flight can genuinely be stopped. False means cancellation can
    /// only stop scheduling further segments; the one being decoded runs to completion.
    /// </summary>
    public bool SupportsCancellation { get; init; }

    /// <summary>
    /// True when the translator actually reads <see cref="TranslationOptions.ContextSegments"/>.
    /// False on everything this product ships, and false is a report rather than a shrug.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Context is the caller's only lever, and a lever that does nothing is worse than no lever:
    /// somebody asks for two segments of context, gets a different-looking translation because
    /// beam search is not deterministic across inputs it never saw, and concludes the flag works.
    /// So a translator that ignores it says so, and the caller reports the option as ignored — the
    /// same shape as the diariser being told a speaker count it cannot use.
    /// </para>
    /// <para>
    /// The recommended checkpoint is why it is false. It is a sentence-level model with no way to
    /// mark which part of its input is context, so folding preceding segments in would translate
    /// them too and leave the caller splitting one English paragraph back into its parts by guess.
    /// Nothing has measured what context buys here, and an unmeasured decode path is the one thing
    /// this feature cannot afford: every published figure is a no-context figure.
    /// </para>
    /// </remarks>
    public bool HonoursContext { get; init; }

    /// <summary>
    /// Longest source the model will accept, counted in the model's own tokens. Null when it has
    /// no such limit.
    /// </summary>
    /// <remarks>
    /// This guards an edge rather than the common case. At the 14.6 characters per second this
    /// project's own ASR output runs to, a full 30-second segment projects to at most about 190
    /// tokens against the recommended tokenizer's declared 512, and measured peaks on real Spanish
    /// and German segments agree at 189. Two things keep that a projection — the density figures
    /// come from written text, and 23 of the 25 languages have had no audio through this pipeline
    /// at all — so the limit is carried and a source that exceeds it is refused with
    /// <see cref="SegmentTooLongException"/>. It is never truncated: half a sentence translated
    /// fluently is the failure this whole contract exists to avoid.
    /// </remarks>
    public int? MaxSourceTokens { get; init; }

    /// <summary>
    /// BCP-47 tags the model claims to read. Empty when unconstrained or unknown, and membership
    /// is a claim about a list rather than about quality: the recommended checkpoint's own card
    /// disclaims its coverage list in as many words.
    /// </summary>
    public IReadOnlyList<string> SourceLanguages { get; init; } = [];

    /// <summary>
    /// BCP-47 tags the model can write. This product only ever asks for
    /// <see cref="TranslationTarget.LanguageTag"/>; the list is here so a translator that can do
    /// more says so rather than hiding it.
    /// </summary>
    public IReadOnlyList<string> TargetLanguages { get; init; } = [TranslationTarget.LanguageTag];
}

/// <summary>
/// Turns a finished transcript into an English one. The one abstraction the rest of the
/// application knows about for translation: no detail of the checkpoint, the tokenizer or ONNX
/// Runtime may leak through it, for the same reason <see cref="ITranscriptionEngine"/> hides
/// parakeet.cpp and <c>ISpeakerLabeller</c> hides the diariser.
/// </summary>
/// <remarks>
/// <para>
/// <b>It takes segments, never audio.</b> Translation reads what the ASR wrote; a translator that
/// opened the file would be a second speech model, which is a different product decision (and the
/// one Whisper's translate task would force). That is also what makes the pass free of the audio
/// pipeline entirely: no second read, no resampling, no segmentation.
/// </para>
/// <para>
/// <b>It returns segments rather than turns.</b> Unlike a speaker labeller, whose output is an
/// annotation something else has to apply, a translated segment <i>is</i> the displayable artefact:
/// it is what the pane shows and what the formatters write. Streaming them out through
/// <see cref="IAsyncEnumerable{T}"/> is the same choice the ASR engine makes, and for the same
/// reason — a long transcript renders as it is produced.
/// </para>
/// <para>
/// <b>It runs last.</b> Decode, then label speakers, then translate. That order belongs to the
/// code rather than to taste: <c>SpeakerAssignment</c> attributes a speaker per word and cuts
/// segments where the speaker changes, and a translated segment has no words, so translating first
/// would quietly coarsen every label rather than fail where anyone could see it. Every yielded
/// segment therefore carries the <see cref="TranscriptSegment.Speaker"/> it arrived with.
/// </para>
/// </remarks>
public interface ITranscriptTranslator : IAsyncDisposable
{
    TranslatorCapabilities Capabilities { get; }

    /// <summary>Loads the model. Idempotent, expensive, never on a UI thread.</summary>
    ValueTask LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Translates every segment, in order, yielding one translated segment per source segment with
    /// its times, its source index and its speaker unchanged. A segment the model returns nothing
    /// for is yielded with empty text rather than dropped: the counts have to line up, because a
    /// pass that loses entries silently loses transcript.
    /// </summary>
    IAsyncEnumerable<TranscriptSegment> TranslateAsync(
        IReadOnlyList<TranscriptSegment> segments,
        TranslationOptions options,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Thrown when a segment's source exceeds what the model will read. Refusing is the contract:
/// a truncated source translates fluently and says nothing about the half it dropped.
/// </summary>
public sealed class SegmentTooLongException : Exception
{
    public SegmentTooLongException(int segmentIndex, int tokens, int limit)
        : base($"Segment {segmentIndex} is {tokens} tokens, past this translator's limit of {limit}. " +
               "It is refused rather than truncated: a shortened source comes back as fluent English " +
               "with no sign that anything was dropped.")
    {
        SegmentIndex = segmentIndex;
        Tokens = tokens;
        Limit = limit;
    }

    public SegmentTooLongException(string message)
        : base(message)
    {
    }

    public SegmentTooLongException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SegmentTooLongException()
    {
    }

    /// <summary>Index into the segment list handed to the translator, or -1 when unknown.</summary>
    public int SegmentIndex { get; } = -1;

    /// <summary>What the source measured, in the model's own tokens.</summary>
    public int Tokens { get; }

    /// <summary>The limit it passed.</summary>
    public int Limit { get; }
}
