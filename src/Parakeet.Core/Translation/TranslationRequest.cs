using Parakeet.Core.Transcription;

namespace Parakeet.Core.Translation;

/// <summary>
/// Exactly what one segment hands to the model: the source string it reads, and the surrounding
/// text the caller asked to be carried with it.
/// </summary>
/// <remarks>
/// This type exists so that no translator ever builds a source string by hand. The target-language
/// token is mandatory and its absence is invisible — the recommended checkpoint given Spanish
/// without <c>&gt;&gt;eng&lt;&lt;</c> returns fluent German rather than an error — so the marking
/// belongs at the seam, where forgetting it is not an option a translator has, rather than in a
/// comment every implementation is trusted to have read.
/// </remarks>
public sealed record TranslationRequest
{
    /// <summary>Index into the segment list this came from, so a refusal can name it.</summary>
    public required int SegmentIndex { get; init; }

    /// <summary>
    /// What the model reads. Always begins with the translator's target token; built only by
    /// <see cref="Build"/>.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// The text of the preceding segments the caller asked for, oldest first, unmarked — context is
    /// something a decode loop folds in, and only the segment actually being translated carries the
    /// target token. Empty when <see cref="TranslationOptions.ContextSegments"/> is zero, which is
    /// the default.
    /// </summary>
    public required IReadOnlyList<string> Context { get; init; }

    /// <summary>
    /// Builds one request per segment, in order, marking every source with
    /// <paramref name="targetToken"/>.
    /// </summary>
    /// <remarks>
    /// A blank token is refused rather than defaulted. Defaulting it would produce a source string
    /// that looks right, decodes without complaint and comes back in the wrong language, which is
    /// the exact failure the token exists to prevent.
    /// </remarks>
    public static IReadOnlyList<TranslationRequest> Build(
        IReadOnlyList<TranscriptSegment> segments,
        TranslationOptions options,
        string targetToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetToken);
        options.Validate();

        var requests = new List<TranslationRequest>(segments.Count);

        for (var i = 0; i < segments.Count; i++)
        {
            var context = new List<string>(options.ContextSegments);
            var first = Math.Max(0, i - options.ContextSegments);
            for (var j = first; j < i; j++)
            {
                var previous = segments[j].Text.Trim();
                if (previous.Length > 0)
                {
                    context.Add(previous);
                }
            }

            requests.Add(new TranslationRequest
            {
                SegmentIndex = i,
                Source = Mark(segments[i].Text, targetToken),
                Context = context,
            });
        }

        return requests;
    }

    /// <summary>The target token, a space, then the text — with the text's own edges trimmed.</summary>
    public static string Mark(string text, string targetToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetToken);

        return $"{targetToken} {text.Trim()}";
    }

    /// <summary>True when <paramref name="source"/> carries <paramref name="targetToken"/>.</summary>
    public static bool IsMarked(string source, string targetToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetToken);

        return source.StartsWith(targetToken, StringComparison.Ordinal);
    }
}
