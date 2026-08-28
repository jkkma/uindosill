using System.Globalization;
using System.Text.RegularExpressions;
using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// Holds this build's declared diariser limits against the sidecar's own constants.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SidecarSpeakerLabeller.DeclaredLimits"/> is a second copy of two values that belong
/// to the Python engine, and it exists because the window has to state them before anything is
/// loaded — and on a machine with no bundled Python, before there is anything to ask. The copy is
/// policed twice: at load, by <c>SidecarSpeakerLabeller</c> refusing a sidecar that reports
/// different values, and here, by reading the constants out of the source.
/// </para>
/// <para>
/// <b>Both values are <c>None</c> now, and that is what makes this worth keeping rather than what
/// makes it pointless.</b> Until 2026-08-27 they were 4 and 3000 — the ONNX diariser's speaker
/// slots and the fifty minutes its labels had been established to — and a drift between the two
/// halves would have put a wrong number in front of a user. A drift now would be worse in a quieter
/// way: a cap or a bound appearing on one side only would be this build claiming an established
/// limit for an engine nothing has measured. A null is a claim too, and it is held to the same way.
/// </para>
/// <para>
/// The load-time check fires on the machine where the two halves are together, which is a user's.
/// This one fires in CI, which is where a drift should be caught — a check that only runs after
/// shipping is a check that reports history.
/// </para>
/// <para>
/// It reads the files rather than importing them, because this suite has no Python:
/// <c>scripts/cloud-setup.sh</c> installs the SDK and PowerShell and no interpreter.
/// </para>
/// </remarks>
public sealed class DeclaredLimitsTests
{
    [Fact]
    public void TheDeclaredCapIsTheOneTheSidecarShips()
    {
        Assert.Null(SidecarSpeakerLabeller.DeclaredLimits.MaxSpeakers);
        AssertEngineConstantIsNone("MAX_SPEAKERS");
    }

    [Fact]
    public void TheDeclaredEstablishedLengthIsTheOneTheSidecarShips()
    {
        Assert.Null(SidecarSpeakerLabeller.DeclaredLimits.ReliableUpTo);
        AssertEngineConstantIsNone("RELIABLE_UP_TO_SECONDS");
    }

    [Fact]
    public void TheReportedLimitsAreTheConstantsRatherThanLiteralsBesideThem()
    {
        // Without this, the copy being policed is the middle one of three. `capabilities()` is what
        // the host is actually told, and it could be edited to a literal — `"maxSpeakers": 8` — while
        // the module constant stayed None and both checks above kept passing. Asserting that the
        // dict names the constants is what makes the chain constant -> reply -> declaration unbroken.
        //
        // They are read through `engine_module` rather than defined beside the dict, which is where
        // they moved when the second diariser arrived: the values belong to the engine that has
        // them, not to the package that reports them.
        Assert.Contains("\"maxSpeakers\": engine_module.MAX_SPEAKERS", Source, StringComparison.Ordinal);
        Assert.Contains(
            "\"reliableUpToSeconds\": engine_module.RELIABLE_UP_TO_SECONDS", Source, StringComparison.Ordinal);
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
    /// Asserts a module constant in the engine is spelled <c>None</c>, rather than absent.
    /// </summary>
    /// <remarks>
    /// The distinction is the point. A constant that had been deleted would leave
    /// <c>capabilities()</c> raising <c>AttributeError</c> at load — loud, and not this test's
    /// business — but one quietly given a number would ship a bound this project has not measured,
    /// which nothing else would catch. So this fails on a missing constant too: an engine that
    /// stopped declaring its limits at all is a change worth stopping for either way.
    /// </remarks>
    private static void AssertEngineConstantIsNone(string name)
    {
        var match = Regex.Match(
            EngineSource, $@"^{Regex.Escape(name)}\s*=\s*(.+?)\s*$", RegexOptions.Multiline);

        Assert.True(
            match.Success,
            $"{name} was not found in diariser/pyannote_engine.py. Either it was renamed — in which case this " +
            "build's declared limits are describing a constant that no longer exists — or its shape changed past " +
            "what this check can read. Both are worth stopping for.");

        Assert.Equal("None", match.Groups[1].Value);
    }

    private static string EngineSource { get; } = File.ReadAllText(
        Path.Combine(RepositoryRoot, "python", "uindosill_engines", "diariser", "pyannote_engine.py"));

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
