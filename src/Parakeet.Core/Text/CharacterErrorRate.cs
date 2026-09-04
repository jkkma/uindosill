// Compiled twice, like WordAlignment.cs and WordErrorRate.cs: by Parakeet.Core, and by
// `Add-Type -Path` from the scripts. Own usings, own `#nullable enable`, BCL only, nothing else
// from Parakeet.Core.
#nullable enable
using System;
using System.Collections.Generic;

namespace Parakeet.Core.Text;

/// <summary>
/// One scored comparison in characters rather than words: <c>(S + D + I) / N</c> with <c>N</c> the
/// reference length in characters. Same definition as <see cref="WordErrorRateResult"/> and the
/// same reason it can exceed one.
/// </summary>
public sealed record CharacterErrorRateResult
{
    /// <summary>Characters in the reference after normalisation — the denominator.</summary>
    public required int ReferenceCharacters { get; init; }

    /// <summary>Characters in the hypothesis after normalisation.</summary>
    public required int HypothesisCharacters { get; init; }

    public required int Substitutions { get; init; }

    public required int Deletions { get; init; }

    public required int Insertions { get; init; }

    public int Errors => Substitutions + Deletions + Insertions;

    /// <summary>
    /// The character error rate as a fraction, or <see cref="double.NaN"/> when the reference is
    /// empty — a rate over nothing is not zero, for the same reason it is not in
    /// <see cref="WordErrorRateResult.Rate"/>.
    /// </summary>
    public double Rate => ReferenceCharacters == 0 ? double.NaN : (double)Errors / ReferenceCharacters;
}

/// <summary>
/// Character error rate over already-normalised character sequences: align, then count.
/// Normalise first with <see cref="TranscriptNormalizer.CharacterErrorRateTokens"/> — this takes
/// tokens rather than text for the same reason <see cref="WordErrorRate"/> does, so the
/// normalisation a score was computed under is visible at the call site.
///
/// <para><b>Why this exists beside the word error rate.</b> Japanese, Chinese and Thai are written
/// without spaces between words, so the word-based metric has no denominator to work with: measured
/// on 2026-09-04 over FLEURS, <see cref="TranscriptNormalizer.WordErrorRateTokens"/> yields
/// <b>3.55 tokens per sentence</b> on <c>ja_jp</c> against <b>22.01</b> on <c>en_us</c>, so a single
/// wrong character costs roughly 28% of a sentence and an unpunctuated line scores 0 or 1 and
/// nothing between. Character error rate is what the field publishes for those languages, and it
/// is what this must be scored with.</para>
///
/// <para><b>A character error rate and a word error rate are different numbers and must never be
/// set beside one another</b> — not in a table column, not in a sentence comparing two models, not
/// in <c>docs/UNPROVEN.md</c>. They do not measure the same thing and the character figure is
/// smaller by construction on the same transcript. Anything reporting either must name which it
/// is, alongside its corpus and the normalisation it was computed under.</para>
/// </summary>
public static class CharacterErrorRate
{
    public static CharacterErrorRateResult Score(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        if (reference is null) throw new ArgumentNullException(nameof(reference));
        if (hypothesis is null) throw new ArgumentNullException(nameof(hypothesis));

        // The alignment is token-agnostic: it compares strings ordinally and does not care that
        // each one here happens to be a single character. Reusing it rather than writing a second
        // Levenshtein means the character path inherits the divide-and-conquer, the prefix and
        // suffix trimming, and the tests that hold them to a brute-force oracle.
        var summary = WordAlignment.Summarize(WordAlignment.Align(reference, hypothesis));
        return new CharacterErrorRateResult
        {
            ReferenceCharacters = reference.Count,
            HypothesisCharacters = hypothesis.Count,
            Substitutions = summary.Substitutions,
            Deletions = summary.Deletions,
            Insertions = summary.Insertions,
        };
    }

    /// <summary>
    /// The corpus figure: every count summed, so the rate is total errors over total reference
    /// characters — a long file weighs more than a short one, exactly as in
    /// <see cref="WordErrorRate.Aggregate"/>. A mean of per-file rates is a different number and
    /// is not computed here.
    /// </summary>
    public static CharacterErrorRateResult Aggregate(IEnumerable<CharacterErrorRateResult> results)
    {
        if (results is null) throw new ArgumentNullException(nameof(results));

        int reference = 0, hypothesis = 0, substitutions = 0, deletions = 0, insertions = 0;
        foreach (var result in results)
        {
            if (result is null) throw new ArgumentException("Results may not contain null.", nameof(results));
            reference += result.ReferenceCharacters;
            hypothesis += result.HypothesisCharacters;
            substitutions += result.Substitutions;
            deletions += result.Deletions;
            insertions += result.Insertions;
        }

        return new CharacterErrorRateResult
        {
            ReferenceCharacters = reference,
            HypothesisCharacters = hypothesis,
            Substitutions = substitutions,
            Deletions = deletions,
            Insertions = insertions,
        };
    }
}
