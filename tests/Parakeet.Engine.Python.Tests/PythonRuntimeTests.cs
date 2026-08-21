using System.Runtime.InteropServices;
using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// Where the interpreter is looked for, and what is said when it is not there.
/// </summary>
/// <remarks>
/// The messages are the point rather than incidental. "The bundled Python is not there" and
/// "UINDOSILL_PYTHON points at something that is not a file" send a reader in opposite directions,
/// and the first is a reinstall while the second is a typo in the reader's own shell.
/// </remarks>
[Collection("environment")]
public sealed class PythonRuntimeTests : IDisposable
{
    private readonly string? _interpreter = Environment.GetEnvironmentVariable(PythonRuntime.InterpreterVariable);
    private readonly string? _packages = Environment.GetEnvironmentVariable(PythonRuntime.PackagesVariable);

    public PythonRuntimeTests()
    {
        // These tests are about the environment variables, so they set them — and the collection
        // above is what stops two of them doing it at once. Restored in Dispose, because a variable
        // left set would silently change every later test in this process.
        Environment.SetEnvironmentVariable(PythonRuntime.InterpreterVariable, null);
        Environment.SetEnvironmentVariable(PythonRuntime.PackagesVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PythonRuntime.InterpreterVariable, _interpreter);
        Environment.SetEnvironmentVariable(PythonRuntime.PackagesVariable, _packages);
    }

    private static string ExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python.exe" : "bin/python3";

    /// <summary>A directory laid out the way the installer lays the bundle out.</summary>
    private static string StageBundle()
    {
        var root = Directory.CreateTempSubdirectory("uindosill-bundle").FullName;
        var interpreter = Path.Combine(root, "python", ExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(interpreter)!);
        File.WriteAllText(interpreter, "not really an interpreter");
        Directory.CreateDirectory(Path.Combine(root, "python", "uindosill_engines"));
        return root;
    }

    [Fact]
    public void TheBundleBesideTheApplicationIsWhatIsFoundWithoutAnOverride()
    {
        var root = StageBundle();

        var resolved = PythonRuntime.Resolve(root);

        Assert.Equal(Path.Combine(root, "python", ExecutableName), resolved.Interpreter);
        Assert.Equal(Path.Combine(root, "python"), resolved.PackageRoot);
        Assert.False(resolved.Overridden);
    }

    [Fact]
    public void AMissingBundleSaysItIsMissingAndSaysWhatToDoInstead()
    {
        var empty = Directory.CreateTempSubdirectory("uindosill-empty").FullName;

        var failure = Assert.Throws<PythonSidecarException>(() => PythonRuntime.Resolve(empty));

        Assert.Contains("The bundled Python is not at", failure.Message, StringComparison.Ordinal);
        Assert.Contains(PythonRuntime.InterpreterVariable, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverrideThatPointsAtNothingIsBlamedOnTheOverrideRatherThanOnTheBundle()
    {
        // The two messages must not be interchangeable. Somebody who set the variable needs to hear
        // about the variable; somebody who did not needs to hear about their install.
        var missing = Path.Combine(Path.GetTempPath(), "uindosill-no-such-interpreter");
        Environment.SetEnvironmentVariable(PythonRuntime.InterpreterVariable, missing);

        var failure = Assert.Throws<PythonSidecarException>(() => PythonRuntime.Resolve(StageBundle()));

        Assert.Contains(PythonRuntime.InterpreterVariable, failure.Message, StringComparison.Ordinal);
        Assert.Contains("which is not a file", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInterpreterWithNoPackageBesideItSaysWhichHalfIsMissing()
    {
        var root = StageBundle();
        Directory.Delete(Path.Combine(root, "python", "uindosill_engines"));

        var failure = Assert.Throws<PythonSidecarException>(() => PythonRuntime.Resolve(root));

        Assert.Contains("uindosill_engines", failure.Message, StringComparison.Ordinal);
        Assert.Contains(PythonRuntime.PackagesVariable, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverridesAreReadBeforeTheBundleAndSayThatTheyWere()
    {
        // The harnesses drive the same protocol out of a venv that is not the bundle, and a run that
        // used one has to be able to say so: a figure taken against an unknown interpreter is a
        // figure nobody can reproduce.
        var bundle = StageBundle();
        var other = StageBundle();

        Environment.SetEnvironmentVariable(
            PythonRuntime.InterpreterVariable, Path.Combine(other, "python", ExecutableName));
        Environment.SetEnvironmentVariable(
            PythonRuntime.PackagesVariable, Path.Combine(other, "python"));

        var resolved = PythonRuntime.Resolve(bundle);

        Assert.Equal(Path.Combine(other, "python", ExecutableName), resolved.Interpreter);
        Assert.True(resolved.Overridden);
    }

    [Fact]
    public void TryResolveReportsTheReasonInsteadOfThrowingIt()
    {
        // What the window needs: a checkbox cannot be drawn out of an exception, and a missing
        // bundle is a reason to disable an opt-in with the reason beside it rather than to crash a
        // binding getter.
        var empty = Directory.CreateTempSubdirectory("uindosill-empty").FullName;

        Assert.False(PythonRuntime.TryResolve(out var resolution, out var reason, empty));

        Assert.Null(resolution);
        Assert.Contains("The bundled Python is not at", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveSaysNothingWhenThereIsNothingToSay()
    {
        Assert.True(PythonRuntime.TryResolve(out var resolution, out var reason, StageBundle()));

        Assert.NotNull(resolution);
        Assert.Null(reason);
    }
}

/// <summary>
/// The one thing in this assembly that is not free of shared state.
/// </summary>
/// <remarks>
/// <see cref="PythonRuntimeTests"/> sets process-wide environment variables, which is the only way
/// to test that they are read at all. Everything else here uses a per-test temporary directory
/// handed over through the resolution, and runs in parallel.
/// </remarks>
[CollectionDefinition("environment", DisableParallelization = true)]
public sealed class EnvironmentCollection;
