using Parakeet.Core.Transcription;

namespace Parakeet.Core.Translation;

/// <summary>
/// Drives a translator over a finished transcript and returns the English one, holding the
/// translator to the contract on the way through.
/// </summary>
/// <remarks>
/// The checks below are the reason this is a driver rather than a loop each caller writes. A
/// translator that returns the wrong number of segments, moves one in time, or hands back the
/// source's word timings under new text produces a file that looks entirely correct and is not —
/// the same class of failure <c>SegmentingTranscriptionEngine</c> refuses a mismatched batch for.
/// Every one of them is caught here, once, in front of every caller.
/// </remarks>
public static class TranscriptTranslation
{
    /// <summary>
    /// Translates <paramref name="document"/>'s segments and returns a document carrying the
    /// English text, the target language and the translator's model id as provenance. The source
    /// document is unchanged; the caller keeps it if it wants both.
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

        var source = document.Segments;
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
