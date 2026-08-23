using System.Globalization;
using System.Text;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Formatting;

/// <summary>
/// Markdown transcript with a provenance header. The header is not decoration: quantisation
/// quality on this engine is measured on one corpus only (docs/UNPROVEN.md), so a transcript
/// that does not record which weights and backend produced it cannot be judged later.
/// </summary>
public sealed class MarkdownFormatter : ITranscriptFormatter
{
    public string Id => "md";

    public string DisplayName => "Markdown";

    public string FileExtension => ".md";

    public string Format(TranscriptDocument document, TranscriptFormatOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= TranscriptFormatOptions.Default;

        var nl = options.NewLine;
        var builder = new StringBuilder();

        var title = string.IsNullOrWhiteSpace(document.SourceName) ? "Transcript" : document.SourceName;
        builder.Append("# ").Append(title).Append(nl).Append(nl);

        if (options.IncludeMetadata)
        {
            var rows = new List<(string Key, string Value)>();
            if (document.AudioDuration is { } duration)
            {
                rows.Add(("Duration", Timecode.ToClock(duration)));
            }

            if (document.ModelId is { } model)
            {
                rows.Add(("Model", model));
            }

            if (document.Quantisation is { } quantisation)
            {
                rows.Add(("Quantisation", quantisation));
            }

            if (document.Backend is { } backend)
            {
                rows.Add(("Backend", backend.ToString().ToLowerInvariant()));
            }

            if (document.Language is { } language)
            {
                rows.Add(("Language", language));
            }

            if (document.RealTimeFactor is { } rtf)
            {
                rows.Add(("Real-time factor", rtf.ToString("0.###", CultureInfo.InvariantCulture)));
            }

            // The model's own, beside the whole pass's: the read and the segmentation are in the
            // row above and not in this one.
            if (document.DecodeRealTimeFactor is { } decodeRtf)
            {
                rows.Add(("Decode real-time factor", decodeRtf.ToString("0.###", CultureInfo.InvariantCulture)));
            }

            // Which model named the speakers, beside which model wrote the words, for the same
            // reason: a label whose source is unknown cannot be re-examined.
            if (document.SpeakerModelId is { } speakerModel)
            {
                rows.Add(("Speaker labels", speakerModel));
            }

            // And on which provider, because the model does not settle what the labels are: the
            // same graph scores a different diarisation error rate on each of the three.
            if (document.SpeakerBackend is { } speakerBackend)
            {
                rows.Add(("Speaker backend", speakerBackend.ToString().ToLowerInvariant()));
            }

            // The count a human supplied, recorded even when the model had already satisfied it:
            // an archived transcript that folded nothing still had its labels constrained, and a
            // row that only appeared when a merge happened would hide exactly that case.
            if (document.RequestedSpeakerCount is { } requestedSpeakers)
            {
                rows.Add(("Speaker count requested", requestedSpeakers.ToString(CultureInfo.InvariantCulture)));
            }

            // And which labels it joined, with the evidence for each. The same sentence the command
            // line and the window print, built in the one place that builds it.
            if (document.SpeakerFolds.Count > 0)
            {
                rows.Add(("Speaker folds", string.Join("; ", document.SpeakerFolds.Select(f => f.Describe()))));
            }

            // And which model wrote the English, when this is not what the engine heard. A reader
            // holding a translation without knowing it is reading a second model's opinion of a
            // first model's output, which is a different thing to judge.
            if (document.TranslatedTo is { } target)
            {
                rows.Add(("Translated into", target));
            }

            if (document.TranslationModelId is { } translationModel)
            {
                rows.Add(("Translation model", translationModel));
            }

            if (document.TranslationBackend is { } translationBackend)
            {
                rows.Add(("Translation backend", translationBackend.ToString().ToLowerInvariant()));
            }

            if (document.TranslationDecode is { } translationDecode)
            {
                rows.Add(("Translation decode", translationDecode));
            }

            if (rows.Count > 0)
            {
                builder.Append("| Field | Value |").Append(nl);
                builder.Append("|---|---|").Append(nl);
                foreach (var (key, value) in rows)
                {
                    builder.Append("| ").Append(key).Append(" | ").Append(Escape(value)).Append(" |").Append(nl);
                }

                builder.Append(nl);
            }
        }

        foreach (var segment in document.Segments)
        {
            if (segment.IsEmpty)
            {
                continue;
            }

            if (options.IncludeTimestamps)
            {
                builder.Append("**[").Append(Timecode.ToClock(segment.Start)).Append("]** ");
            }

            if (segment.Speaker is { } speaker)
            {
                builder.Append("**").Append(Escape(speaker)).Append(":** ");
            }

            builder.Append(Escape(segment.Text.Trim())).Append(nl).Append(nl);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escapes only the characters that would change the rendered structure. Transcripts are
    /// prose; escaping every Markdown metacharacter makes real speech unreadable.
    /// </summary>
    private static string Escape(string text) =>
        text.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
