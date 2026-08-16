// Compiled twice, like WordAlignment.cs: by Parakeet.Core, and by `Add-Type -Path` from the
// scripts. Own usings, own `#nullable enable`, BCL only, nothing else from Parakeet.Core.
#nullable enable
using System;
using System.Collections.Generic;

namespace Parakeet.Core.Text;

/// <summary>
/// One scored comparison: a hypothesis against a reference, as counts. The rate is
/// <c>(S + D + I) / N</c> with <c>N</c> the reference length, which is the definition everyone
/// uses and the reason it can exceed one — an insertion counts against a denominator it is not in.
/// </summary>
public sealed record WordErrorRateResult
{
    /// <summary>Tokens in the reference after normalisation — the denominator.</summary>
    public required int ReferenceWords { get; init; }

    /// <summary>Tokens in the hypothesis after normalisation.</summary>
    public required int HypothesisWords { get; init; }

    public required int Substitutions { get; init; }

    public required int Deletions { get; init; }

    public required int Insertions { get; init; }

    public int Errors => Substitutions + Deletions + Insertions;

    /// <summary>
    /// The word error rate as a fraction, or <see cref="double.NaN"/> when the reference is empty:
    /// a rate over nothing is not zero, and printing it as zero would read as a perfect score.
    /// </summary>
    public double Rate => ReferenceWords == 0 ? double.NaN : (double)Errors / ReferenceWords;
}

/// <summary>
/// Word error rate over already-normalised token sequences: align, then count. Normalise first
/// with <see cref="TranscriptNormalizer.WordErrorRateTokens"/> — this deliberately takes tokens,
/// not text, so the normalisation a score was computed under is visible at the call site.
/// </summary>
public static class WordErrorRate
{
    public static WordErrorRateResult Score(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        if (reference is null) throw new ArgumentNullException(nameof(reference));
        if (hypothesis is null) throw new ArgumentNullException(nameof(hypothesis));

        var summary = WordAlignment.Summarize(WordAlignment.Align(reference, hypothesis));
        return new WordErrorRateResult
        {
            ReferenceWords = reference.Count,
            HypothesisWords = hypothesis.Count,
            Substitutions = summary.Substitutions,
            Deletions = summary.Deletions,
            Insertions = summary.Insertions,
        };
    }

    /// <summary>
    /// The corpus figure: every count summed, so the rate is total errors over total reference
    /// words. That weights a long file more than a short one, which is what "WER over the corpus"
    /// means everywhere it is reported; a mean of per-file rates would be a different number and
    /// is not computed here.
    /// </summary>
    public static WordErrorRateResult Aggregate(IEnumerable<WordErrorRateResult> results)
    {
        if (results is null) throw new ArgumentNullException(nameof(results));

        int reference = 0, hypothesis = 0, substitutions = 0, deletions = 0, insertions = 0;
        foreach (var result in results)
        {
            if (result is null) throw new ArgumentException("Results may not contain null.", nameof(results));
            reference += result.ReferenceWords;
            hypothesis += result.HypothesisWords;
            substitutions += result.Substitutions;
            deletions += result.Deletions;
            insertions += result.Insertions;
        }

        return new WordErrorRateResult
        {
            ReferenceWords = reference,
            HypothesisWords = hypothesis,
            Substitutions = substitutions,
            Deletions = deletions,
            Insertions = insertions,
        };
    }
}
