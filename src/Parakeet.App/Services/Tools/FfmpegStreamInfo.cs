using System.Globalization;
using System.Text.RegularExpressions;

namespace Parakeet.App.Services.Tools;

/// <summary>Reads subtitle codecs from the input section of the bundled FFmpeg's stream report.</summary>
internal static partial class FfmpegStreamInfo
{
    // ffprobe is not part of the shipped tool drop. FFmpeg's zero-duration null remux reports
    // the same input streams without decoding media or writing a file. Match only stream rows,
    // not indented metadata values or the subsequent output's newly assigned stream numbers.
    [GeneratedRegex(@"^  Stream #0:(\d+)(?:\[[^\]]*\])?(?:\([^)]*\))?: Subtitle: ([a-zA-Z0-9_]+)(?:\s|,|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SubtitleRow();

    public static IReadOnlyList<string> SubtitleCodecs(IEnumerable<string> lines)
    {
        var foundInput = false;
        var streams = new SortedDictionary<int, string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("Input #0,", StringComparison.Ordinal))
            {
                foundInput = true;
                continue;
            }

            if (!foundInput)
            {
                continue;
            }

            if (line.StartsWith("Stream mapping:", StringComparison.Ordinal)
                || line.StartsWith("Output #", StringComparison.Ordinal))
            {
                break;
            }

            var match = SubtitleRow().Match(line);
            if (match.Success)
            {
                if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || !streams.TryAdd(index, match.Groups[2].Value))
                {
                    throw new SubtitleMuxException("The recording's subtitle stream order could not be determined.");
                }
            }
        }

        if (!foundInput)
        {
            throw new SubtitleMuxException("The recording's existing subtitle tracks could not be inspected.");
        }

        return [.. streams.Values];
    }
}
