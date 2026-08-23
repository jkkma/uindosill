using System.Globalization;

namespace Parakeet.App.ViewModels;

/// <summary>
/// A position in a recording, as the window writes it: <c>mm:ss</c>, and <c>h:mm:ss</c> once there
/// is an hour to show.
/// </summary>
/// <remarks>
/// <para>
/// One place rather than several, because a transcript and its transport have to agree: a cue
/// reading <c>04:05</c> beside a clock reading <c>4:05</c> is two formats for the same instant, and
/// the whole point of a clickable citation is that the two are the same number.
/// </para>
/// <para>
/// Invariant, like every other number this window formats. The interface is English throughout,
/// and a machine whose locale writes a different digit group would otherwise produce a timestamp
/// that does not match the ones in the transcript files beside it.
/// </para>
/// </remarks>
internal static class Timecode
{
    public static string Format(TimeSpan value)
    {
        // Clamped rather than signed. Nothing in a recording happens before it starts, and a
        // negative timestamp on a cue would be a bug shown as text rather than caught.
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var hours = (int)value.TotalHours;

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{value.Minutes:00}:{value.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.Minutes:00}:{value.Seconds:00}");
    }
}
