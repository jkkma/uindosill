// This file is compiled twice: by the Parakeet.Core project, and by `Add-Type -Path` from
// scripts/compare-transcripts.ps1 and scripts/word-distance.ps1, which load it straight from the
// source tree so that they need no build. That is why it carries its own usings and its own
// `#nullable enable`, references nothing outside the BCL and nothing else in Parakeet.Core, and
// avoids syntax newer than the PowerShell 7 compiler is guaranteed to have. Keep it that way, or
// the scripts stop running on the machines that have transcripts to compare.
#nullable enable
using System;
using System.Collections.Generic;

namespace Parakeet.Core.Text;

/// <summary>What one step of an alignment does to get from the reference to the hypothesis.</summary>
public enum AlignmentOpKind
{
    /// <summary>The same token on both sides.</summary>
    Match,

    /// <summary>A reference token replaced by a different hypothesis token.</summary>
    Substitute,

    /// <summary>A reference token with nothing opposite it in the hypothesis.</summary>
    Delete,

    /// <summary>A hypothesis token with nothing opposite it in the reference.</summary>
    Insert,
}

/// <summary>
/// One step of an alignment. The indexes are positions in the reference and hypothesis
/// sequences; the side an operation does not touch carries -1, so a <see cref="AlignmentOpKind.Delete"/>
/// has no hypothesis index and an <see cref="AlignmentOpKind.Insert"/> no reference index.
/// </summary>
public readonly record struct AlignmentOp(AlignmentOpKind Kind, int ReferenceIndex, int HypothesisIndex);

/// <summary>The counts an alignment reduces to.</summary>
public sealed record AlignmentSummary(int Matches, int Substitutions, int Deletions, int Insertions)
{
    /// <summary>Substitutions, deletions and insertions together — the edit distance.</summary>
    public int Edits => Substitutions + Deletions + Insertions;
}

/// <summary>
/// Word-level Levenshtein distance and alignment between two token sequences.
///
/// <para>Comparing two transcripts of the same audio by word index is exact only while the two
/// agree on how many words there are and where; a single inserted word desynchronises every pair
/// after it, and a total-count check cannot see insertions and deletions that cancel out.
/// <c>docs/UNPROVEN.md</c> records that shape producing 727 "differences" where 50 existed. An
/// alignment does not assume anything about where the tokens line up: it finds the cheapest way to
/// edit one sequence into the other and reports every match, substitution, deletion and
/// insertion on the way. The word error rate is the same alignment with a human transcript on
/// the reference side.</para>
///
/// <para>Two entry points. <see cref="Distance"/> is the count alone, in two rows of memory.
/// <see cref="Align"/> is the full edit script, in linear memory via Hirschberg's divide-and-conquer
/// — three hours of audio is roughly 30,000 tokens a side, and a plain backtrace matrix for that
/// would be 900 MB. Time is O(n·m) either way, which is seconds in C# for the three-hour case.
/// Where several alignments are equally cheap this picks one deterministically; the total edit
/// count is the same for all of them, so anything computed from the count is unaffected by the
/// choice, and only which pairs are called substitutions rather than a deletion beside an
/// insertion can differ.</para>
///
/// <para>Tokens are compared ordinally and exactly. Case-folding, punctuation and any other
/// normalisation is the caller's job — see <see cref="TranscriptNormalizer"/> — so that the same
/// alignment serves a raw comparison and a normalised one and the two are never silently mixed.</para>
/// </summary>
public static class WordAlignment
{
    /// <summary>
    /// Below this many cells the alignment is a plain matrix with a backtrace, which is faster than
    /// recursion for small inputs and is where every recursion bottoms out. One byte per cell.
    /// </summary>
    internal const int DefaultSmallLimit = 1 << 20;

    /// <summary>The Levenshtein distance between the two sequences, counting one per edit.</summary>
    public static int Distance(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        if (reference is null) throw new ArgumentNullException(nameof(reference));
        if (hypothesis is null) throw new ArgumentNullException(nameof(hypothesis));

        _ = Intern(reference, hypothesis, out var a, out var b);

        // Near-identical sequences share long runs at both ends. Trimming them costs one pass and
        // can turn a very large matrix into a small one; on genuinely divergent input it finds
        // nothing and the whole matrix is walked.
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;

        var aEnd = a.Length;
        var bEnd = b.Length;
        while (aEnd > start && bEnd > start && a[aEnd - 1] == b[bEnd - 1]) { aEnd--; bEnd--; }

        var n = aEnd - start;
        var m = bEnd - start;
        if (n == 0) return m;
        if (m == 0) return n;

        var last = PrefixCosts(a, b, start, aEnd, start, bEnd);
        return last[m];
    }

    /// <summary>
    /// The cheapest edit script from <paramref name="reference"/> to <paramref name="hypothesis"/>,
    /// in order. Every reference index appears exactly once as a match, substitution or deletion
    /// and every hypothesis index exactly once as a match, substitution or insertion, both in
    /// increasing order, so the result can be replayed against either side.
    /// </summary>
    public static IReadOnlyList<AlignmentOp> Align(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
        => Align(reference, hypothesis, DefaultSmallLimit);

    /// <summary>
    /// <see cref="Align(IReadOnlyList{string}, IReadOnlyList{string})"/> with the matrix cut-over
    /// exposed, so a test can force the recursive path on inputs small enough to check by hand.
    /// </summary>
    internal static IReadOnlyList<AlignmentOp> Align(
        IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis, int smallLimit)
    {
        if (reference is null) throw new ArgumentNullException(nameof(reference));
        if (hypothesis is null) throw new ArgumentNullException(nameof(hypothesis));
        if (smallLimit < 1) throw new ArgumentOutOfRangeException(nameof(smallLimit));

        _ = Intern(reference, hypothesis, out var a, out var b);
        var ops = new List<AlignmentOp>(Math.Max(a.Length, b.Length));

        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start])
        {
            ops.Add(new AlignmentOp(AlignmentOpKind.Match, start, start));
            start++;
        }

        var aEnd = a.Length;
        var bEnd = b.Length;
        var suffix = 0;
        while (aEnd > start && bEnd > start && a[aEnd - 1] == b[bEnd - 1]) { aEnd--; bEnd--; suffix++; }

        AlignRange(a, b, start, aEnd, start, bEnd, smallLimit, ops);

        for (var k = 0; k < suffix; k++)
        {
            ops.Add(new AlignmentOp(AlignmentOpKind.Match, aEnd + k, bEnd + k));
        }

        return ops;
    }

    /// <summary>Counts the operations of an alignment.</summary>
    public static AlignmentSummary Summarize(IReadOnlyList<AlignmentOp> operations)
    {
        if (operations is null) throw new ArgumentNullException(nameof(operations));

        int matches = 0, substitutions = 0, deletions = 0, insertions = 0;
        for (var i = 0; i < operations.Count; i++)
        {
            switch (operations[i].Kind)
            {
                case AlignmentOpKind.Match: matches++; break;
                case AlignmentOpKind.Substitute: substitutions++; break;
                case AlignmentOpKind.Delete: deletions++; break;
                case AlignmentOpKind.Insert: insertions++; break;
                default: throw new InvalidOperationException($"Unknown alignment operation {operations[i].Kind}.");
            }
        }

        return new AlignmentSummary(matches, substitutions, deletions, insertions);
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps every distinct token to a small integer once, so the inner loops compare integers
    /// rather than strings. On thirty thousand tokens a side that is the difference between the
    /// comparison being the cost and the loop being the cost.
    /// </summary>
    private static Dictionary<string, int> Intern(
        IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis, out int[] a, out int[] b)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        a = new int[reference.Count];
        b = new int[hypothesis.Count];

        for (var i = 0; i < reference.Count; i++) a[i] = IdOf(ids, reference[i]);
        for (var j = 0; j < hypothesis.Count; j++) b[j] = IdOf(ids, hypothesis[j]);
        return ids;
    }

    private static int IdOf(Dictionary<string, int> ids, string token)
    {
        if (token is null) throw new ArgumentException("Token sequences may not contain null.");
        if (!ids.TryGetValue(token, out var id))
        {
            id = ids.Count;
            ids.Add(token, id);
        }

        return id;
    }

    private static void AlignRange(
        int[] a, int[] b, int aLo, int aHi, int bLo, int bHi, int smallLimit, List<AlignmentOp> ops)
    {
        var n = aHi - aLo;
        var m = bHi - bLo;

        if (n == 0)
        {
            for (var j = bLo; j < bHi; j++) ops.Add(new AlignmentOp(AlignmentOpKind.Insert, -1, j));
            return;
        }

        if (m == 0)
        {
            for (var i = aLo; i < aHi; i++) ops.Add(new AlignmentOp(AlignmentOpKind.Delete, i, -1));
            return;
        }

        if (n == 1 || m == 1 || (long)n * m <= smallLimit)
        {
            AlignSmall(a, b, aLo, aHi, bLo, bHi, ops);
            return;
        }

        // Hirschberg: split the reference in half, find the hypothesis split that makes the two
        // halves' costs add up to the optimum, and recurse. The optimum for the whole range is
        // min over j of prefix[j] + suffix[j], and the j that attains it is a point the optimal
        // path passes through.
        var mid = aLo + n / 2;
        var prefix = PrefixCosts(a, b, aLo, mid, bLo, bHi);
        var suffix = SuffixCosts(a, b, mid, aHi, bLo, bHi);

        var split = 0;
        var best = int.MaxValue;
        for (var j = 0; j <= m; j++)
        {
            var cost = prefix[j] + suffix[j];
            if (cost < best)
            {
                best = cost;
                split = j;
            }
        }

        AlignRange(a, b, aLo, mid, bLo, bLo + split, smallLimit, ops);
        AlignRange(a, b, mid, aHi, bLo + split, bHi, smallLimit, ops);
    }

    /// <summary>
    /// The last row of the edit matrix: <c>result[j]</c> is the distance between
    /// <c>a[aLo..aHi)</c> and the first <c>j</c> tokens of <c>b[bLo..bHi)</c>.
    /// </summary>
    private static int[] PrefixCosts(int[] a, int[] b, int aLo, int aHi, int bLo, int bHi)
    {
        var m = bHi - bLo;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (var j = 0; j <= m; j++) prev[j] = j;

        for (var i = aLo; i < aHi; i++)
        {
            cur[0] = prev[0] + 1;
            var ai = a[i];
            for (var j = 1; j <= m; j++)
            {
                var best = prev[j - 1] + (ai == b[bLo + j - 1] ? 0 : 1);
                var del = prev[j] + 1;
                if (del < best) best = del;
                var ins = cur[j - 1] + 1;
                if (ins < best) best = ins;
                cur[j] = best;
            }

            (prev, cur) = (cur, prev);
        }

        return prev;
    }

    /// <summary>
    /// The mirror image: <c>result[j]</c> is the distance between <c>a[aLo..aHi)</c> and the tokens
    /// of <c>b[bLo..bHi)</c> from position <c>j</c> to the end. Computed by walking both sequences
    /// backwards, without copying either.
    /// </summary>
    private static int[] SuffixCosts(int[] a, int[] b, int aLo, int aHi, int bLo, int bHi)
    {
        var m = bHi - bLo;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (var k = 0; k <= m; k++) prev[k] = k;

        for (var i = aHi - 1; i >= aLo; i--)
        {
            cur[0] = prev[0] + 1;
            var ai = a[i];
            for (var k = 1; k <= m; k++)
            {
                // The suffix of length k starts at bHi - k, and that is the token it adds.
                var best = prev[k - 1] + (ai == b[bHi - k] ? 0 : 1);
                var del = prev[k] + 1;
                if (del < best) best = del;
                var ins = cur[k - 1] + 1;
                if (ins < best) best = ins;
                cur[k] = best;
            }

            (prev, cur) = (cur, prev);
        }

        // prev[k] is indexed by suffix length; the caller wants it by suffix start.
        var byStart = new int[m + 1];
        for (var j = 0; j <= m; j++) byStart[j] = prev[m - j];
        return byStart;
    }

    private const byte Diagonal = 0;
    private const byte Up = 1;
    private const byte Left = 2;

    /// <summary>
    /// The plain matrix with a one-byte-per-cell backtrace. Ties are broken the same way every
    /// time — diagonal first, then deletion, then insertion — so the same input always gives the
    /// same edit script.
    /// </summary>
    private static void AlignSmall(int[] a, int[] b, int aLo, int aHi, int bLo, int bHi, List<AlignmentOp> ops)
    {
        var n = aHi - aLo;
        var m = bHi - bLo;
        var width = m + 1;
        var direction = new byte[(n + 1) * width];
        var prev = new int[width];
        var cur = new int[width];

        for (var j = 0; j <= m; j++)
        {
            prev[j] = j;
            direction[j] = Left;
        }

        for (var i = 1; i <= n; i++)
        {
            cur[0] = i;
            direction[i * width] = Up;
            var ai = a[aLo + i - 1];
            for (var j = 1; j <= m; j++)
            {
                var best = prev[j - 1] + (ai == b[bLo + j - 1] ? 0 : 1);
                var dir = Diagonal;
                var del = prev[j] + 1;
                if (del < best) { best = del; dir = Up; }
                var ins = cur[j - 1] + 1;
                if (ins < best) { best = ins; dir = Left; }
                cur[j] = best;
                direction[i * width + j] = dir;
            }

            (prev, cur) = (cur, prev);
        }

        // Walk back from the corner, then reverse what was collected.
        var first = ops.Count;
        var row = n;
        var col = m;
        while (row > 0 || col > 0)
        {
            var dir = direction[row * width + col];
            if (row > 0 && col > 0 && dir == Diagonal)
            {
                row--;
                col--;
                var kind = a[aLo + row] == b[bLo + col] ? AlignmentOpKind.Match : AlignmentOpKind.Substitute;
                ops.Add(new AlignmentOp(kind, aLo + row, bLo + col));
            }
            else if (row > 0 && (col == 0 || dir == Up))
            {
                row--;
                ops.Add(new AlignmentOp(AlignmentOpKind.Delete, aLo + row, -1));
            }
            else
            {
                col--;
                ops.Add(new AlignmentOp(AlignmentOpKind.Insert, -1, bLo + col));
            }
        }

        ops.Reverse(first, ops.Count - first);
    }
}
