using System.Globalization;
using System.Text.RegularExpressions;
using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// Holds this build's declared diariser limits against the sidecar's own constants.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SidecarSpeakerLabeller.DeclaredLimits"/> is a second copy of two numbers that belong
/// to the Python engine, and it exists because the window has to warn about them before anything is
/// loaded — and on a machine with no bundled Python, before there is anything to ask. The copy is
/// policed twice: at load, by <c>SidecarSpeakerLabeller</c> refusing a sidecar that reports
/// different numbers, and here, by reading the constants out of the source.
/// </para>
/// <para>
/// The load-time check fires on the machine where the two halves are together, which is a user's.
/// This one fires in CI, which is where a drift should be caught — a check that only runs after
/// shipping is a check that reports history.
/// </para>
/// <para>
/// It reads the file rather than importing it, because this suite has no Python:
/// <c>scripts/cloud-setup.sh</c> installs the SDK and PowerShell and no interpreter. Two integers
/// in two <c>#:</c>-documented module constants are well within what a regular expression can be
/// trusted with, and a rename that put them out of its reach fails this test rather than passing
/// it silently.
/// </para>
/// </remarks>
public sealed class DeclaredLimitsTests
{
    [Fact]
    public void TheDeclaredCapIsTheOneTheSidecarShips()
    {
        Assert.Equal(SidecarSpeakerLabeller.DeclaredLimits.MaxSpeakers, ReadConstant("MAX_SPEAKERS"));
    }

    [Fact]
    public void TheDeclaredEstablishedLengthIsTheOneTheSidecarShips()
    {
        var seconds = ReadConstant("RELIABLE_UP_TO_SECONDS");

        Assert.Equal(SidecarSpeakerLabeller.DeclaredLimits.ReliableUpTo, TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void TheReportedLimitsAreTheConstantsRatherThanLiteralsBesideThem()
    {
        // Without this, the copy being policed is the middle one of three. `capabilities()` is what
        // the host is actually told, and it could be edited to a literal — `"maxSpeakers": 8` — while
        // the module constant above it stayed 4 and both checks in this file kept passing. Asserting
        // that the dict names the constants is what makes the chain constant → reply → declaration
        // unbroken.
        Assert.Contains("\"maxSpeakers\": MAX_SPEAKERS", Source, StringComparison.Ordinal);
        Assert.Contains("\"reliableUpToSeconds\": RELIABLE_UP_TO_SECONDS", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCapMatchesTheSpeakerCountTheGraphIsDrivenWith()
    {
        // A third copy, and the one that is not a report but a shape: `engine.py`'s N_SPK is the
        // width of the probability array the streaming loop allocates and slices. If it and
        // MAX_SPEAKERS ever disagreed, the host would warn about a cap the model does not have —
        // or, worse, not warn about one it does.
        var geometry = File.ReadAllText(
            Path.Combine(RepositoryRoot, "python", "uindosill_engines", "diariser", "engine.py"));
        var match = Regex.Match(geometry, @"^N_SPK\s*=\s*([0-9]+)\s*$", RegexOptions.Multiline);

        Assert.True(match.Success, "N_SPK was not found in diariser/engine.py.");
        Assert.Equal(
            SidecarSpeakerLabeller.DeclaredLimits.MaxSpeakers,
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheSidecarStillSaysTheCountCannotBeFixed()
    {
        // The third field of the declaration, and the one that is a flag rather than a number. It is
        // what makes --speaker-count report that it folded the labels afterwards instead of appearing
        // to steer the model.
        Assert.False(SidecarSpeakerLabeller.DeclaredLimits.SupportsFixedSpeakerCount);
        Assert.Contains(
            "\"supportsFixedSpeakerCount\": False",
            Source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Evaluates a constant written as either an integer or a product of them — the file spells
    /// fifty minutes <c>50 * 60</c>, which is clearer than 3000 and is why this does the arithmetic
    /// rather than demanding a literal.
    /// </summary>
    private static int ReadConstant(string name)
    {
        var match = Regex.Match(Source, $@"^{Regex.Escape(name)}\s*=\s*([0-9][0-9 *]*)\s*$", RegexOptions.Multiline);

        Assert.True(
            match.Success,
            $"{name} was not found in diariser/__init__.py. Either it was renamed — in which case this build's " +
            "declared limits are describing a constant that no longer exists — or its shape changed past what this " +
            "check can read. Both are worth stopping for.");

        return match.Groups[1].Value
            .Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.Parse(part, CultureInfo.InvariantCulture))
            .Aggregate(1, (product, part) => product * part);
    }

    private static string Source { get; } = File.ReadAllText(
        Path.Combine(RepositoryRoot, "python", "uindosill_engines", "diariser", "__init__.py"));

    /// <summary>The directory holding <c>Uindosill.slnx</c>, found by walking up from the test binary.</summary>
    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Uindosill.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new DirectoryNotFoundException(
                    $"No Uindosill.slnx above {AppContext.BaseDirectory}. This test reads the sidecar's source, so " +
                    "it needs the repository rather than only the build output.");
        }
    }
}
