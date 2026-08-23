using Parakeet.Core.Transcription;

namespace Parakeet.Core.Translation;

/// <summary>
/// Drives a translator over a finished transcript and returns the English one, holding the
/// translator to the contract on the way through.
/// </summary>
/// <remarks>
/// <para>
/// The checks below are the reason this is a driver rather than a loop each caller writes. A
/// translator that returns the wrong number of segments, moves one in time, or hands back the
/// source's word timings under new text produces a file that looks entirely correct and is not —
/// the same class of failure <c>SegmentingTranscriptionEngine</c> refuses a mismatched batch for.
/// Every one of them is caught here, once, in front of every caller.
/// </para>
/// <para>
/// <b>The unit of translation is the sentence, since 2026-08-23.</b> Until that day the translator
/// was given the recogniser's segments, and on audio cut at the thirty-second cap a segment held
/// nine sentences, so the English came back as one string per segment with no word timings and
/// nothing to cut it by — the Ask tab read the transcript by the sentence and the English by the
/// segment, and a documentary's three German lines at 02:43, 02:48 and 02:53 were one English line
/// at 02:43. Now the source is split with <see cref="SentenceSplitter"/> first — the same cut the
/// transcript's lines are made with, on the word timings the model reported — and the translator
/// sees one request per sentence. Each English segment keeps its sentence's start and end, its
/// source index and its speaker, and the checks below hold per sentence exactly as they held per
/// segment. A segment the splitter leaves whole — one sentence, no words, words that do not
/// reproduce the text — is translated as before. <see cref="Units"/> is that split, exposed so a
/// caller that compares the source against the English compares the units that were actually
/// paired. What it costs and what it has not been measured on is in <c>docs/UNPROVEN.md</c>.
/// </para>
/// </remarks>
public static class TranscriptTranslation
{
    /// <summary>
    /// The segments the translator is given for <paramref name="document"/>: its segments cut into
    /// sentences where their word timings allow, in order. The English returned by
    /// <see cref="TranslateAsync"/> pairs with these by index, not with the document's own segments.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Units(TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return SentenceSplitter.Split(document.Segments);
    }

    /// <summary>
    /// Translates <paramref name="document"/> a sentence at a time and returns a document carrying
    /// the English text, the target language and the translator's model id as provenance. The
    /// source document is unchanged; the caller keeps it if it wants both.
    /// </summary>
    public static async Task<TranscriptDocument> TranslateAsync(
        TranscriptDocument document,
        ITranscriptTranslator translator,
        TranslationOptions? options = null,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(translator);
        options ??= TranslationOptions.Default;
        options.Validate();

        var capabilities = translator.Capabilities;

        if (capabilities.RequiresSourceLanguage)
        {
            // Nothing in this pipeline can supply it. The transcript's language field records what
            // was requested rather than what was detected, and the ASR's language hint is inert on
            // this checkpoint, so a translator that needs the source language needs something that
            // does not exist here — said plainly rather than guessed at.
            throw new InvalidOperationException(
                $"Translator '{capabilities.EngineName}' has to be told the source language, and nothing in this " +
                "pipeline detects it. Only a many-to-one translator, which is told the target and never the source, " +
                "can run this pass.");
        }

        var source = Units(document);
        var translated = new List<TranscriptSegment>(source.Count);

        await foreach (var segment in translator
            .TranslateAsync(source, options, progress, ct)
            .ConfigureAwait(false))
        {
            var index = translated.Count;
            if (index >= source.Count)
            {
                throw new InvalidOperationException(
                    $"Translator '{capabilities.EngineName}' returned more segments than it was given " +
                    $"({source.Count}). A pass that invents entries invents transcript.");
            }

            translated.Add(Check(capabilities, source[index], segment, index));
        }

        if (translated.Count != source.Count)
        {
            throw new InvalidOperationException(
                $"Translator '{capabilities.EngineName}' returned {translated.Count} segments for {source.Count}. " +
                "A segment the model returns nothing for is yielded empty rather than dropped, because a pass that " +
                "loses entries loses transcript without saying so.");
        }

        return document with
        {
            Segments = translated,
            TranslatedTo = TranslationTarget.LanguageTag,
            TranslationModelId = capabilities.ModelId,

            // The same reason its sibling on the speaker pass is read here: the translator resolves
            // its own provider inside the sidecar, and the capabilities of the loaded engine are
            // where that answer exists.
            TranslationBackend = capabilities.Backend,

            // And the search, for the reason the capability gives: the graphs are pinned and the
            // search is not, and until 2026-08-22 no transcript carried it.
            TranslationDecode = capabilities.DecodeDescription,
        };
    }

    private static TranscriptSegment Check(
        TranslatorCapabilities capabilities,
        TranscriptSegment source,
        TranscriptSegment translated,
        int index)
    {
        if (translated.Start != source.Start || translated.End != source.End)
        {
            throw new InvalidOperationException(
                $"Translator '{capabilities.EngineName}' moved segment {index} from " +
                $"{source.Start}–{source.End} to {translated.Start}–{translated.End}. Translation rewrites text; " +
                "the timeline belongs to the audio and is not the translator's to change.");
        }

        if (translated.SourceSegmentIndex != source.SourceSegmentIndex)
        {
            throw new InvalidOperationException(
                $"Translator '{capabilities.EngineName}' changed segment {index}'s source index from " +
                $"{source.SourceSegmentIndex} to {translated.SourceSegmentIndex}.");
        }

        if (!string.Equals(translated.Speaker, source.Speaker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Translator '{capabilities.EngineName}' changed who said segment {index}, from " +
                $"'{source.Speaker ?? "(nobody)"}' to '{translated.Speaker ?? "(nobody)"}'. Speakers are decided " +
                "before this pass runs and translating a line does not change whose line it is.");
        }

        if (!capabilities.PreservesWordTimings && translated.Words.Count > 0)
        {
            throw new InvalidOperationException(
                $"Translator '{capabilities.EngineName}' says it does not preserve word timings and returned " +
                $"{translated.Words.Count} words on segment {index}. Word timings from before a translation, " +
                "attached to the text after it, are a lie with timestamps on it.");
        }

        return translated;
    }
}
