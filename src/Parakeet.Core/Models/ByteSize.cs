using System.Globalization;

namespace Parakeet.Core.Models;

/// <summary>
/// A byte count as a person reads it — <c>1.34 GiB</c>, <c>452.64 MiB</c>.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in either surface that renders it. The command line has printed sizes since
/// there was a catalogue and the window now prints them too, and a second implementation would be
/// two answers to one question: the same file reported as 1.34 GiB by <c>uindosill models</c> and
/// as 1.3 GB by the Models tab is the kind of small disagreement that makes a user check which one
/// is lying.
/// </para>
/// <para>
/// Binary units, and invariant. Binary because these numbers are compared against what Windows
/// shows in Explorer's properties dialog, which counts the same way; invariant because they are
/// quoted back in bug reports beside run summaries that are invariant too, so a decimal separator
/// taken from the operator's locale would leave one run's own output disagreeing with itself.
/// </para>
/// </remarks>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    /// <summary>The count in the largest unit that leaves it at or above one.</summary>
    public static string Describe(long value)
    {
        double size = value;
        var unit = 0;

        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{size:0.##} {Units[unit]}");
    }
}
