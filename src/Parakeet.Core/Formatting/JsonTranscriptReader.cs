using System.Text.Json;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Formatting;

/// <summary>
/// The inverse of <see cref="JsonTranscriptFormatter"/>: a transcript written in an earlier
/// session, read back beside its media. Until this existed a chat could only be had against a
/// transcript from the current session's queue, and a pinned transcript could be hashed but
/// never loaded.
/// </summary>
/// <remarks>
/// Reads exactly what the formatter writes and tolerates what it does not: unknown properties
/// are skipped, because the formatter has grown fields over its life (speechDetector, the
/// speaker and translation blocks) and a reader that refused an older or newer file on sight
/// would punish exactly the transcript this exists to reopen. Derived values — <c>text</c>,
/// <c>realTimeFactor</c>, <c>decodeRealTimeFactor</c>, each segment's <c>conf</c> — are not
/// read: the document recomputes them from what is, so a file whose derived values disagree
/// with its segments cannot smuggle the disagreement in. Malformed structure throws
/// <see cref="FormatException"/> naming what was wrong, never a best guess: a transcript is
/// the ground every citation stands on, and a silently mis-read one would look perfectly fine.
/// </remarks>
public static class JsonTranscriptReader
{
    public static TranscriptDocument Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        // A FormatException like every other refusal here, so a caller naming the file names it
        // for this too — "(Parameter 'json')" points at nothing a user can act on.
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FormatException("The file is empty. A transcript is a JSON object with a 'segments' array.");
        }

        using var parsed = ParseDocument(json);
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"A transcript is a JSON object, not {Describe(root.ValueKind)}.");
        }

        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("The transcript has no 'segments' array, which every transcript this product writes carries.");
        }

        return new TranscriptDocument
        {
            Segments = ReadSegments(segments),
            SourceName = OptionalString(root, "source"),
            ModelId = OptionalString(root, "model"),
            Quantisation = OptionalString(root, "quantisation"),
            Backend = OptionalBackend(root, "backend"),
            Language = OptionalString(root, "language"),
            AudioDuration = OptionalSeconds(root, "audioDurationSec"),
            ProcessingTime = OptionalSeconds(root, "processingSec"),
            DecodeTime = OptionalSeconds(root, "decodeSec"),
            SpeechDetector = OptionalString(root, "speechDetector"),
            SpeakerModelId = OptionalString(root, "speakerModel"),
            SpeakerBackend = OptionalBackend(root, "speakerBackend"),
            SpeakerEmbeddingBackend = OptionalString(root, "speakerEmbeddingBackend"),
            RequestedSpeakerCount = OptionalInt(root, "requestedSpeakerCount"),
            SpeakerFolds = ReadSpeakerFolds(root),
            SpeakerTurns = ReadSpeakerTurns(root),
            TranslatedTo = OptionalString(root, "translatedTo"),
            TranslationModelId = OptionalString(root, "translationModel"),
            TranslationBackend = OptionalBackend(root, "translationBackend"),
            TranslationDecode = OptionalString(root, "translationDecode"),
        };
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Not JSON: {ex.Message}", ex);
        }
    }

    private static IReadOnlyList<TranscriptSegment> ReadSegments(JsonElement segments)
    {
        var result = new List<TranscriptSegment>(segments.GetArrayLength());
        var index = 0;
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"Segment {index} is {Describe(segment.ValueKind)}, not an object.");
            }

            result.Add(new TranscriptSegment
            {
                Start = RequiredSeconds(segment, "start", $"segment {index}"),
                End = RequiredSeconds(segment, "end", $"segment {index}"),
                Text = RequiredString(segment, "text", $"segment {index}"),
                Speaker = OptionalString(segment, "speaker"),
                Words = ReadWords(segment, index),
            });
            index++;
        }

        return result;
    }

    private static IReadOnlyList<TranscriptWord> ReadWords(JsonElement segment, int segmentIndex)
    {
        if (!segment.TryGetProperty("words", out var words))
        {
            return [];
        }

        if (words.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"Segment {segmentIndex}'s 'words' is {Describe(words.ValueKind)}, not an array.");
        }

        var result = new List<TranscriptWord>(words.GetArrayLength());
        var index = 0;
        foreach (var word in words.EnumerateArray())
        {
            var where = $"segment {segmentIndex}, word {index}";
            if (word.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"{Capitalise(where)} is {Describe(word.ValueKind)}, not an object.");
            }

            result.Add(new TranscriptWord
            {
                Text = RequiredString(word, "w", where),
                Start = RequiredSeconds(word, "start", where),
                End = RequiredSeconds(word, "end", where),
                Confidence = OptionalFloat(word, "conf"),
                Speaker = OptionalString(word, "speaker"),
            });
            index++;
        }

        return result;
    }

    private static IReadOnlyList<SpeakerFold> ReadSpeakerFolds(JsonElement root)
    {
        if (!root.TryGetProperty("speakerFolds", out var folds) || folds.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<SpeakerFold>(folds.GetArrayLength());
        var index = 0;
        foreach (var fold in folds.EnumerateArray())
        {
            var where = $"speaker fold {index}";
            if (fold.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"{Capitalise(where)} is {Describe(fold.ValueKind)}, not an object.");
            }

            result.Add(new SpeakerFold
            {
                Dropped = RequiredString(fold, "from", where),
                Kept = RequiredString(fold, "into", where),
                OverlapSeconds = RequiredDouble(fold, "overlapSec", where),
                RunnerUpSeconds = OptionalDouble(fold, "runnerUpSec"),
            });
            index++;
        }

        return result;
    }

    private static IReadOnlyList<SpeakerTurn> ReadSpeakerTurns(JsonElement root)
    {
        if (!root.TryGetProperty("speakerTurns", out var turns) || turns.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<SpeakerTurn>(turns.GetArrayLength());
        var index = 0;
        foreach (var turn in turns.EnumerateArray())
        {
            var where = $"speaker turn {index}";
            if (turn.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"{Capitalise(where)} is {Describe(turn.ValueKind)}, not an object.");
            }

            result.Add(new SpeakerTurn
            {
                Start = RequiredSeconds(turn, "start", where),
                End = RequiredSeconds(turn, "end", where),
                Speaker = RequiredString(turn, "speaker", where),
            });
            index++;
        }

        return result;
    }

    private static string RequiredString(JsonElement element, string name, string where)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"{Capitalise(where)} has no '{name}' string.");
        }

        return value.GetString()!;
    }

    private static TimeSpan RequiredSeconds(JsonElement element, string name, string where)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException($"{Capitalise(where)} has no '{name}' number.");
        }

        return Seconds(value, name, where);
    }

    private static double RequiredDouble(JsonElement element, string name, string where)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException($"{Capitalise(where)} has no '{name}' number.");
        }

        return value.GetDouble();
    }

    // The Optional* helpers accept absence and JSON null — the formatter has grown fields over
    // its life, and an older file simply lacks the newer ones — but a field that is present with
    // the wrong type is a misread, not an absence, and nulling it silently is exactly the
    // never-a-silent-misread rule the segment readers already keep.
    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"'{name}' is {Describe(value.ValueKind)}, not a string.");
        }

        return value.GetString();
    }

    private static TimeSpan? OptionalSeconds(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException($"'{name}' is {Describe(value.ValueKind)}, not a number.");
        }

        return Seconds(value, name, "the transcript");
    }

    private static int? OptionalInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException($"'{name}' is {Describe(value.ValueKind)}, not a number.");
        }

        return value.GetInt32();
    }

    private static float? OptionalFloat(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException($"'{name}' is {Describe(value.ValueKind)}, not a number.");
        }

        return value.GetSingle();
    }

    private static double? OptionalDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException($"'{name}' is {Describe(value.ValueKind)}, not a number.");
        }

        return value.GetDouble();
    }

    private static ComputeBackend? OptionalBackend(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"'{name}' is {Describe(value.ValueKind)}, not a string.");
        }

        var spelled = value.GetString()!;
        if (!Enum.TryParse<ComputeBackend>(spelled, ignoreCase: true, out var backend))
        {
            // Loud rather than null: a backend this build does not know is provenance it cannot
            // represent, and dropping it would hand the caller a transcript claiming less than
            // its file says.
            throw new FormatException($"'{name}' names a compute backend this build does not know: '{spelled}'.");
        }

        return backend;
    }

    /// <summary>
    /// The formatter writes seconds with three decimals; a decimal read multiplies to ticks
    /// exactly, where a double would round-trip 1.234 through 1.2339999… and land a tick off.
    /// </summary>
    private static TimeSpan Seconds(JsonElement value, string name, string where)
    {
        if (!value.TryGetDecimal(out var seconds))
        {
            throw new FormatException($"{Capitalise(where)}'s '{name}' is not a readable number.");
        }

        // Bounded before the tick multiply, in the one place every time field funnels through.
        // A negative time places nothing, and past the cap the tick arithmetic overflows and the
        // window grid walks unboundedly — while the commonest way such a value really arrives, a
        // timestamp in epoch seconds, sits nine orders of magnitude past a week.
        if (seconds < 0 || seconds > MaxSeconds)
        {
            throw new FormatException(
                $"{Capitalise(where)}'s '{name}' is {value.GetRawText()} seconds, which is not a time inside a recording.");
        }

        return TimeSpan.FromTicks((long)(seconds * TimeSpan.TicksPerSecond));
    }

    /// <summary>A week — longer than any recording this product transcribes.</summary>
    private const decimal MaxSeconds = 7m * 24 * 3600;

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.Null => "null",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "nothing",
    };

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
