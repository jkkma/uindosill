using System.Globalization;

namespace Parakeet.Core.Transcription;

/// <summary>
/// The writing systems this model's languages are written in. parakeet-tdt-0.6b-v3 covers 25
/// European languages, which between them need Latin, Cyrillic and Greek and nothing else, so
/// those are the three worth telling apart. Anything else is <see cref="Other"/> rather than
/// guessed at, and a run of digits or punctuation is <see cref="None"/> rather than forced into
/// a script it does not have.
/// </summary>
public enum TextScript
{
    None,
    Latin,
    Cyrillic,
    Greek,
    Other,
}

/// <summary>Why one segment is worth a second look.</summary>
public enum TranscriptAnomalyKind
{
    /// <summary>The segment is written in a different script from the rest of the transcript.</summary>
    ScriptDisagreement,

    /// <summary>The segment holds words below the confidence threshold.</summary>
    LowConfidence,
}

/// <summary>One reason one segment is worth a second look, carrying the evidence for it.</summary>
public sealed record TranscriptAnomaly
{
    public required TranscriptAnomalyKind Kind { get; init; }

    /// <summary>Index into <see cref="TranscriptDocument.Segments"/>.</summary>
    public required int SegmentIndex { get; init; }

    public required TimeSpan Start { get; init; }

    /// <summary>The evidence in words: which scripts disagreed, or how many words scored low.</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// Finds the segments a reader should check first, using only what the engine already reports.
///
/// <para><b>This measures the text, not the audio.</b> A script disagreement says the decoder
/// emitted Cyrillic where the rest of the transcript is Latin. It does not say the speaker
/// changed language, and on a model that mis-detects, the mis-detection is exactly what you are
/// looking at. Read it as "look here", never as a language identification — the ABI reports no
/// detected language at all (the decode JSON is text, frame_sec, words, tokens), so there is
/// nothing here to identify one with.</para>
///
/// <para>No new threshold is invented. The confidence cutoff is
/// <see cref="TranscriptionOptions.LowConfidenceThreshold"/>, the one knob the project already
/// has and already documents as a guess; everything else on this path is a count of things the
/// engine measured.</para>
/// </summary>
public static class TranscriptAnalysis
{
    private const int ScriptSlots = 5;

    /// <summary>The script one character belongs to, or <see cref="TextScript.None"/> for a
    /// non-letter.</summary>
    public static TextScript ScriptOf(char value)
    {
        if (!char.IsLetter(value))
        {
            return TextScript.None;
        }

        // Cyrillic and Greek are tested before the Latin range because the Latin test is an
        // upper bound, not an interval: every letter below U+0250 that is not Greek or Cyrillic
        // is Latin, which covers ASCII, Latin-1 Supplement and Latin Extended-A/B in one line.
        return value switch
        {
            >= 'Ѐ' and <= 'ԯ' => TextScript.Cyrillic,
            >= 'Ͱ' and <= 'Ͽ' => TextScript.Greek,
            >= 'ἀ' and <= '῿' => TextScript.Greek,
            <= 'ɏ' => TextScript.Latin,
            _ => TextScript.Other,
        };
    }

    /// <summary>
    /// The script most of <paramref name="text"/>'s letters are in, or
    /// <see cref="TextScript.None"/> when it has none.
    /// </summary>
    public static TextScript DominantScript(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Span<int> counts = stackalloc int[ScriptSlots];
        Accumulate(text, counts);
        return Dominant(counts);
    }

    /// <summary>
    /// Every segment worth a second look, script disagreements first. Pass null for
    /// <paramref name="lowConfidenceThreshold"/> to report script only.
    /// </summary>
    public static IReadOnlyList<TranscriptAnomaly> Analyse(
        TranscriptDocument document, float? lowConfidenceThreshold)
    {
        ArgumentNullException.ThrowIfNull(document);

        // The transcript's own script is settled by counting letters across the whole document
        // rather than by a vote of segments, so a single odd segment cannot elect itself the
        // norm and leave every honest segment looking like the anomaly.
        Span<int> whole = stackalloc int[ScriptSlots];
        foreach (var segment in document.Segments)
        {
            Accumulate(segment.Text, whole);
        }

        var documentScript = Dominant(whole);

        var anomalies = new List<TranscriptAnomaly>();

        // One buffer, cleared per segment: a stackalloc inside the loop would grow the frame
        // once per segment and never release until this method returns.
        Span<int> counts = stackalloc int[ScriptSlots];

        for (var i = 0; i < document.Segments.Count; i++)
        {
            var segment = document.Segments[i];
            if (segment.IsEmpty)
            {
                continue;
            }

            counts.Clear();
            Accumulate(segment.Text, counts);
            var script = Dominant(counts);

            // A segment with no letters at all ("2026." on its own) agrees with everything.
            if (documentScript is not TextScript.None
                && script is not TextScript.None
                && script != documentScript)
            {
                anomalies.Add(new TranscriptAnomaly
                {
                    Kind = TranscriptAnomalyKind.ScriptDisagreement,
                    SegmentIndex = i,
                    Start = segment.Start,
                    Detail = $"{script} where the transcript is {documentScript}",
                });
            }

            if (lowConfidenceThreshold is not { } threshold)
            {
                continue;
            }

            var low = 0;
            var scored = 0;
            foreach (var word in segment.Words)
            {
                if (word.Confidence is not { } confidence)
                {
                    continue;
                }

                scored++;
                if (confidence < threshold)
                {
                    low++;
                }
            }

            if (low > 0)
            {
                anomalies.Add(new TranscriptAnomaly
                {
                    Kind = TranscriptAnomalyKind.LowConfidence,
                    SegmentIndex = i,
                    Start = segment.Start,
                    // Invariant, not current culture: this string is read by a person, asserted
                    // by a test and printed beside the rest of the tool's numbers, and a decimal
                    // comma from the machine's locale would break all three.
                    Detail = string.Create(
                        CultureInfo.InvariantCulture, $"{low} of {scored} words below {threshold:0.##}"),
                });
            }
        }

        // ScriptDisagreement sorts before LowConfidence by enum order, which is the order a
        // reader wants them: the rare precise signal before the common fuzzy one.
        return [.. anomalies.OrderBy(a => a.Kind).ThenBy(a => a.SegmentIndex)];
    }

    private static void Accumulate(string text, Span<int> counts)
    {
        foreach (var value in text)
        {
            var script = ScriptOf(value);
            if (script is not TextScript.None)
            {
                counts[(int)script]++;
            }
        }
    }

    private static TextScript Dominant(ReadOnlySpan<int> counts)
    {
        // Slot 0 is None and is never incremented, so an all-punctuation string leaves every
        // count at zero and this returns None.
        var best = TextScript.None;
        var bestCount = 0;

        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] > bestCount)
            {
                bestCount = counts[i];
                best = (TextScript)i;
            }
        }

        return best;
    }
}
