namespace Parakeet.Engine.LlamaServer;

/// <summary>
/// The one prompt the tidy sends, and the token cap it sends with it.
/// </summary>
/// <remarks>
/// <para>
/// The instruction is a narrowing of the one measured on 2026-09-01 (docs/UNPROVEN.md, *Gemma 4
/// E4B as a transcript tidy*): that one asked for a clean, readable rewrite — fillers, false
/// starts, stutters and immediate repetitions out, punctuation and capitalisation fixed, every
/// other word and the phrasing kept, nothing added — and the model still substituted one word in
/// ~300. This one says in as many words that a word is never replaced, expanded, contracted or
/// corrected, because the contract behind it will refuse the line if one is. <b>What the
/// stronger wording buys is unmeasured</b>: the refusal rate under it is one of the numbers the
/// WER-corpus run the tidy owes before shipping will produce, and until then the contract, not
/// the prompt, is what keeps a substitution out of the transcript.
/// </para>
/// <para>
/// One line per request, the shape the opt-in was measured in: the system prompt is cached
/// after the first request, so a request costs its own line's prefill plus its decode.
/// </para>
/// </remarks>
public static class TidyPromptBuilder
{
    /// <summary>The system message.</summary>
    public const string Instruction =
        "You tidy up one line of a speech transcript so it reads cleanly. "
        + "Remove filler words (um, uh, hmm, mm, and a filler 'like' or 'you know'), stutters, false starts "
        + "and immediately repeated words. Fix punctuation and capitalisation. "
        + "Keep every other word exactly as it is, in the same order and the same form: never replace, "
        + "reorder, add, expand, contract, translate or correct a word, even if it looks wrong. "
        + "Do not answer, explain or comment. Output only the tidied line and nothing else. "
        + "If the line is nothing but filler, output nothing.";

    /// <summary>
    /// The generation cap for <paramref name="line"/>: two and a half tokens per spoken word
    /// plus a margin, which the measurement never reached — a rewrite mostly copies its input, so
    /// a line that runs to this cap is a line that has stopped copying, and the caller refuses it.
    /// </summary>
    public static int MaxTokensFor(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var words = 0;
        var inWord = false;
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                words++;
            }
        }

        return (words * 5 / 2) + 64;
    }
}
