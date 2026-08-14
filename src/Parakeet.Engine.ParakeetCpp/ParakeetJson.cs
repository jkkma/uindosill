using System.Text.Json;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.ParakeetCpp;

/// <summary>One clip's decode, as parakeet.cpp reports it.</summary>
internal sealed record ParakeetClipResult
{
    public required string Text { get; init; }

    /// <summary>
    /// Encoder frame stride in seconds (<c>hop_length * subsampling_factor / sample_rate</c>),
    /// 0.08 for the 0.6B models.
    /// </summary>
    /// <remarks>
    /// The engine supplies this, so nothing in this codebase derives or verifies a subsampling
    /// factor. Frame-unit thresholds are multiplied by it to get seconds.
    /// </remarks>
    public double? FrameSeconds { get; init; }

    public IReadOnlyList<TranscriptWord> Words { get; init; } = [];
}

/// <summary>
/// Parses the JSON documents the C ABI returns.
/// </summary>
/// <remarks>
/// Tolerant on purpose: a missing <c>words</c> array means no word timestamps, not a failure,
/// and an unrecognised field is ignored rather than fatal. What is not tolerated is a document
/// that is not the expected shape at all — that means the ABI changed, and continuing would
/// produce a transcript with invented timings.
/// </remarks>
internal static class ParakeetJson
{
    public static IReadOnlyList<ParakeetClipResult> ParseBatch(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);

        using var document = Parse(json);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            var results = new List<ParakeetClipResult>();
            foreach (var element in root.EnumerateArray())
            {
                results.Add(ParseClip(element));
            }

            return results;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            // A single-clip document where a batch was expected. Accepting it keeps a
            // one-segment file working rather than failing on an off-by-one in the ABI.
            return [ParseClip(root)];
        }

        throw new ParakeetNativeException(
            $"Expected a JSON array of clip documents from the batch decode, got {root.ValueKind}.");
    }

    public static ParakeetClipResult ParseClip(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ParakeetNativeException($"Expected a clip object, got {element.ValueKind}.");
        }

        var text = element.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
            ? textElement.GetString() ?? string.Empty
            : string.Empty;

        double? frameSeconds = element.TryGetProperty("frame_sec", out var frame) && frame.ValueKind == JsonValueKind.Number
            ? frame.GetDouble()
            : null;

        var words = ParseWords(element);

        return new ParakeetClipResult
        {
            Text = text,
            FrameSeconds = frameSeconds,
            Words = words,
        };
    }

    private static IReadOnlyList<TranscriptWord> ParseWords(JsonElement element)
    {
        if (!element.TryGetProperty("words", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var words = new List<TranscriptWord>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = item.TryGetProperty("w", out var w) && w.ValueKind == JsonValueKind.String
                ? w.GetString()
                : null;

            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var start = ReadSeconds(item, "start");
            var end = ReadSeconds(item, "end");

            // An end before its start is not survivable downstream: it produces subtitle cues
            // players silently drop. Collapse it to a zero-length word and let the cue builder
            // give it a nominal duration.
            if (end < start)
            {
                end = start;
            }

            float? confidence = item.TryGetProperty("conf", out var conf) && conf.ValueKind == JsonValueKind.Number
                ? (float)conf.GetDouble()
                : null;

            words.Add(new TranscriptWord
            {
                Text = text,
                Start = start,
                End = end,
                Confidence = confidence,
            });
        }

        return words;
    }

    private static TimeSpan ReadSeconds(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return TimeSpan.Zero;
        }

        var seconds = value.GetDouble();
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            var preview = json.Length <= 200 ? json : json[..200] + "…";
            throw new ParakeetNativeException(
                $"parakeet.cpp returned something that is not JSON: {preview}", ex);
        }
    }
}
