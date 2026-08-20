using Parakeet.Core.Text;
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
                // Normalised the same way the source is. Context is text the model reads, so a
                // compound number word left in it is the same hazard one segment later.
                var previous = GermanNumberWords.ToDigits(segments[j].Text).Trim();
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

    /// <summary>
    /// The target token, a space, then the text — with the text's own edges trimmed and its German
    /// compound number words turned into digits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rewrite is here, at the one funnel every source string passes through, for the same
    /// reason the target token is: something a translator is trusted to remember is something a
    /// translator will one day forget. <see cref="GermanNumberWords"/> says why it is needed — the
    /// recogniser writes a spoken year as <c>neunzehnhundertneunundzwanzig</c> and the translator
    /// turns it into a century — and why it is safe to run without knowing the source language.
    /// </para>
    /// <para>
    /// It is unconditional rather than a flag, because it is measured to be a no-op on anything
    /// that is not a German compound: over all 25 FLEURS <c>test</c> configs — 20,146 rows, 8,499
    /// distinct sentences, every language the catalogue claims — it changed nothing. That check is
    /// <c>GermanNumberWordsTests.ItChangesNothingInFleursWrittenText</c> and it is re-runnable, so
    /// the claim that the shipping path still sends the translator the sentences the published
    /// chrF++ figures describe is a test rather than a memory.
    /// </para>
    /// </remarks>
    public static string Mark(string text, string targetToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetToken);

        return $"{targetToken} {GermanNumberWords.ToDigits(text).Trim()}";
    }

    /// <summary>True when <paramref name="source"/> carries <paramref name="targetToken"/>.</summary>
    public static bool IsMarked(string source, string targetToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetToken);

        return source.StartsWith(targetToken, StringComparison.Ordinal);
    }
}
