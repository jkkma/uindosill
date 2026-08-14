using System.Globalization;

namespace Parakeet.Core.Formatting;

/// <summary>Subtitle timecode formatting. Invariant culture, always.</summary>
public static class Timecode
{
    /// <summary>SubRip: <c>HH:MM:SS,mmm</c>, at least two hour digits, comma before millis.</summary>
    public static string ToSrt(TimeSpan value) => Format(value, ',');

    /// <summary>WebVTT: <c>HH:MM:SS.mmm</c>, dot before millis.</summary>
    public static string ToVtt(TimeSpan value) => Format(value, '.');

    /// <summary>Compact display form for Markdown and plain text: <c>HH:MM:SS</c>.</summary>
    public static string ToClock(TimeSpan value)
    {
        var clamped = Clamp(value);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)clamped.TotalHours:00}:{clamped.Minutes:00}:{clamped.Seconds:00}");
    }

    private static string Format(TimeSpan value, char millisecondSeparator)
    {
        var clamped = Clamp(value);

        // TotalHours rather than Hours: a nine-hour recording is unusual but a timecode that
        // silently wraps at 24 hours is a corrupt subtitle file, not an unusual one.
        var hours = (int)clamped.TotalHours;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:00}:{clamped.Minutes:00}:{clamped.Seconds:00}{millisecondSeparator}{clamped.Milliseconds:000}");
    }

    /// <summary>
    /// Negative times cannot be expressed in either format. They mean an upstream bug, but a
    /// subtitle file that a player refuses to open loses the whole transcript, so clamp.
    /// </summary>
    private static TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
