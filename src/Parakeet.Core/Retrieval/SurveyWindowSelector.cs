namespace Parakeet.Core.Retrieval;

/// <summary>
/// Picks an evenly spread subset of a recording's cover windows, as many as a character budget
/// allows — the evidence for a question about the whole recording when reading all of it will not
/// fit.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this fills was measured before it was built.</b> A question about the whole
/// recording is routed to the whole-transcript path only when the recording fits the retrieval
/// tier's context — about 39,000 characters, roughly 25 to 30 minutes of speech — and a
/// three-hour episode is five times that. Until 2026-08-27 such a question fell back to ordinary
/// retrieval: the eight windows that best matched it, which for "summarise this" is eight windows
/// chosen by a scorer with nothing to rank on. The alternative was reading all of it, measured at
/// 1,112.6 s of prefill on the second machine (docs/UNPROVEN.md). Neither is a summary of a
/// three-hour recording.
/// </para>
/// <para>
/// What this returns is the third option: the recording end to end at reduced resolution. Every
/// window is real, contiguous and citable exactly as before, so the citation contract is
/// untouched — what changes is that the model sees a sample of the whole rather than all of a
/// part. <b>The prompt says so</b>, because a sample presented as a transcript is the failure
/// this project cares most about: the model must know there are gaps so it does not narrate the
/// recording as though it read every minute.
/// </para>
/// <para>
/// <b>Even by position, not by score.</b> Ranking would defeat the purpose — the point is
/// coverage, and a scored subset of a global question is the fallback this replaces.
/// </para>
/// </remarks>
public static class SurveyWindowSelector
{
    /// <summary>
    /// The evenly spread subset of <paramref name="cover"/> whose text fits
    /// <paramref name="budgetChars"/>, first and last window always included when more than one
    /// fits. Returns everything when it already fits, and never returns nothing when there is
    /// anything to return.
    /// </summary>
    public static IReadOnlyList<TranscriptWindow> Select(
        IReadOnlyList<TranscriptWindow> cover, int budgetChars)
    {
        ArgumentNullException.ThrowIfNull(cover);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetChars);

        if (cover.Count == 0)
        {
            return [];
        }

        if (Total(cover) <= budgetChars)
        {
            return cover;
        }

        // Windows are not the same size, so the count that fits cannot be divided out — it is
        // searched for. Downward from the average-sized estimate, which is at most a few steps
        // wrong and never loops long: the first count whose actual text fits, wins.
        var average = Math.Max(1, Total(cover) / cover.Count);
        var estimate = Math.Clamp(budgetChars / average, 1, cover.Count);

        for (var take = estimate; take >= 1; take--)
        {
            var picked = EvenlySpread(cover, take);
            if (Total(picked) <= budgetChars)
            {
                return picked;
            }
        }

        // Even one window is over budget. One window of a recording is a poor survey, but it is
        // evidence, and the alternative — nothing — is an abstention about a recording that does
        // answer the question.
        return [cover[0]];
    }

    /// <summary>
    /// <paramref name="take"/> windows spread across <paramref name="cover"/> by position, ends
    /// included. The opening and the close of a recording are where a summary is most often
    /// wrong, so they are never the ones dropped.
    /// </summary>
    private static List<TranscriptWindow> EvenlySpread(IReadOnlyList<TranscriptWindow> cover, int take)
    {
        if (take >= cover.Count)
        {
            return [.. cover];
        }

        if (take == 1)
        {
            return [cover[0]];
        }

        var picked = new List<TranscriptWindow>(take);
        var last = -1;
        for (var i = 0; i < take; i++)
        {
            // Ends inclusive: i = 0 gives the first window and i = take - 1 the last.
            var index = (int)Math.Round(i * (cover.Count - 1) / (double)(take - 1));
            if (index > last)
            {
                picked.Add(cover[index]);
                last = index;
            }
        }

        return picked;
    }

    private static int Total(IReadOnlyList<TranscriptWindow> windows)
    {
        var total = 0;
        foreach (var window in windows)
        {
            total += window.Text.Length;
        }

        return total;
    }
}
