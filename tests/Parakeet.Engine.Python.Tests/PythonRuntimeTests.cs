using System.Runtime.InteropServices;
using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// Where the interpreter is looked for, and what is said when it is not there.
/// </summary>
/// <remarks>
/// <para>
/// The messages are the point rather than incidental. "The bundled Python is not there" and
/// "UINDOSILL_PYTHON points at something that is not a file" send a reader in opposite directions,
/// and the first is a reinstall while the second is a typo in the reader's own shell.
/// </para>
/// <para>
/// Every one of these hands over both directories. The resolver's second place is under
/// <c>%LOCALAPPDATA%</c>, and a test that let it read the real one would pass or fail depending on
/// whether whoever ran the suite had downloaded a bundle — which is the same class of dependency as
/// needing weights, and this assembly has none.
/// </para>
/// </remarks>
[Collection("environment")]
public sealed class PythonRuntimeTests : IDisposable
{
    private readonly string? _interpreter = Environment.GetEnvironmentVariable(PythonRuntime.InterpreterVariable);
    private readonly string? _packages = Environment.GetEnvironmentVariable(PythonRuntime.PackagesVariable);
    private readonly string? _cudaPack = Environment.GetEnvironmentVariable(PythonRuntime.CudaPackVariable);

    public PythonRuntimeTests()
    {
        // These tests are about the environment variables, so they set them — and the collection
        // above is what stops two of them doing it at once. Restored in Dispose, because a variable
        // left set would silently change every later test in this process.
        //
        // **The CUDA one is cleared here for a sharper reason than symmetry.** It is the variable a
        // developer on a machine with a pack is most likely to have set in their own shell, and
        // every assertion below about `CudaPackRoot` being null would fail for them and pass in CI.
        Environment.SetEnvironmentVariable(PythonRuntime.InterpreterVariable, null);
        Environment.SetEnvironmentVariable(PythonRuntime.PackagesVariable, null);
        Environment.SetEnvironmentVariable(PythonRuntime.CudaPackVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PythonRuntime.InterpreterVariable, _interpreter);
        Environment.SetEnvironmentVariable(PythonRuntime.PackagesVariable, _packages);
        Environment.SetEnvironmentVariable(PythonRuntime.CudaPackVariable, _cudaPack);
    }

    /// <summary>A directory laid out the way an unpacked CUDA pack is: torch, and nothing else.</summary>
    private static string StageCudaPack(string root)
    {
        var torch = Path.Combine(root, PythonRuntime.CudaPackDirectoryName, "torch");
        Directory.CreateDirectory(torch);
        File.WriteAllText(Path.Combine(torch, "__init__.py"), "# not really torch");
        return root;
    }

    private static string ExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python.exe" : "bin/python3";

    /// <summary>A directory laid out the way the installer lays the bundle out.</summary>
    private static string StageBundle()
    {
        var root = TestTemp.NewDirectory("uindosill-bundle");
        var interpreter = Path.Combine(root, "python", ExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(interpreter)!);
        File.WriteAllText(interpreter, "not really an interpreter");
        Directory.CreateDirectory(Path.Combine(root, "python", "uindosill_engines"));
        return root;
    }

    /// <summary>A directory with no bundle in it, standing in for either of the two places.</summary>
    private static string StageNothing() =>
        TestTemp.NewDirectory("uindosill-empty");

    [Fact]
    public void TheBundleBesideTheApplicationIsWhatIsFoundWithoutAnOverride()
    {
        var root = StageBundle();

        var resolved = PythonRuntime.Resolve(root, StageNothing());

        Assert.Equal(Path.Combine(root, "python", ExecutableName), resolved.Interpreter);
        Assert.Equal(Path.Combine(root, "python"), resolved.PackageRoot);
        Assert.Equal(PythonRuntime.BundleSource.Application, resolved.Source);
        Assert.False(resolved.Overridden);
    }

    [Fact]
    public void ADownloadedBundleUnderUserDataIsFoundWhenTheApplicationHasNone()
    {
        // The decision of 2026-08-21: the CLI zip carries no interpreter, so the bundle is a third
        // download, and this is where it is meant to be unpacked.
        var downloaded = StageBundle();

        var resolved = PythonRuntime.Resolve(StageNothing(), downloaded);

        Assert.Equal(Path.Combine(downloaded, "python", ExecutableName), resolved.Interpreter);
        Assert.Equal(PythonRuntime.BundleSource.UserData, resolved.Source);
        Assert.False(resolved.Overridden);
    }

    [Fact]
    public void TheApplicationsOwnBundleWinsOverADownloadedOne()
    {
        // An installed desktop copy ships its own and must not start using a download that may be a
        // different version — the two are pinned together, and only one of them was tested.
        var application = StageBundle();
        var downloaded = StageBundle();

        var resolved = PythonRuntime.Resolve(application, downloaded);

        Assert.Equal(Path.Combine(application, "python", ExecutableName), resolved.Interpreter);
        Assert.Equal(PythonRuntime.BundleSource.Application, resolved.Source);
    }

    [Fact]
    public void TheVariableMayNameABundleDirectoryAndThenAnswersBothHalves()
    {
        // What a user with a downloaded bundle in a directory of their own choosing sets. A bundle
        // is one thing, so pointing at it should not take two variables.
        var downloaded = StageBundle();
        Environment.SetEnvironmentVariable(
            PythonRuntime.InterpreterVariable, Path.Combine(downloaded, "python"));

        var resolved = PythonRuntime.Resolve(StageNothing(), StageNothing());

        Assert.Equal(Path.Combine(downloaded, "python", ExecutableName), resolved.Interpreter);
        Assert.Equal(Path.Combine(downloaded, "python"), resolved.PackageRoot);
        Assert.Equal(PythonRuntime.BundleSource.Environment, resolved.Source);
        Assert.True(resolved.Overridden);
    }

    [Fact]
    public void ADirectoryWithNoInterpreterInItIsBlamedOnTheDirectory()
    {
        // Pointed at the bundle's parent rather than the bundle: a plausible mistake, and one whose
        // message has to say what a bundle looks like rather than repeat the path back.
        var downloaded = StageBundle();
        Environment.SetEnvironmentVariable(PythonRuntime.InterpreterVariable, downloaded);

        var failure = Assert.Throws<PythonSidecarException>(
            () => PythonRuntime.Resolve(StageNothing(), StageNothing()));

        Assert.Contains("is read as a bundle", failure.Message, StringComparison.Ordinal);
        Assert.Contains(ExecutableName, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingBundleSaysBothPlacesItLookedAndWhatToDoInstead()
    {
        var application = StageNothing();
        var userData = StageNothing();

        var failure = Assert.Throws<PythonSidecarException>(
            () => PythonRuntime.Resolve(application, userData));

        Assert.Contains("The bundled Python is not at", failure.Message, StringComparison.Ordinal);
        Assert.Contains(application, failure.Message, StringComparison.Ordinal);
        Assert.Contains(userData, failure.Message, StringComparison.Ordinal);
        Assert.Contains(PythonRuntime.InterpreterVariable, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverrideThatPointsAtNothingIsBlamedOnTheOverrideRatherThanOnTheBundle()
    {
        // The two messages must not be interchangeable. Somebody who set the variable needs to hear
        // about the variable; somebody who did not needs to hear about their install.
        var missing = Path.Combine(Path.GetTempPath(), "uindosill-no-such-interpreter");
        Environment.SetEnvironmentVariable(PythonRuntime.InterpreterVariable, missing);

        var failure = Assert.Throws<PythonSidecarException>(
            () => PythonRuntime.Resolve(StageBundle(), StageNothing()));

        Assert.Contains(PythonRuntime.InterpreterVariable, failure.Message, StringComparison.Ordinal);
        Assert.Contains("neither a file nor a directory", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInterpreterWithNoPackageBesideItSaysWhichHalfIsMissing()
    {
        var root = StageBundle();
        Directory.Delete(Path.Combine(root, "python", "uindosill_engines"));

        var failure = Assert.Throws<PythonSidecarException>(
            () => PythonRuntime.Resolve(root, StageNothing()));

        Assert.Contains("uindosill_engines", failure.Message, StringComparison.Ordinal);
        Assert.Contains("half a bundle", failure.Message, StringComparison.Ordinal);
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

        var resolved = PythonRuntime.Resolve(bundle, StageNothing());

        Assert.Equal(Path.Combine(other, "python", ExecutableName), resolved.Interpreter);
        Assert.True(resolved.Overridden);
        Assert.Contains(
            PythonRuntime.InterpreterVariable, resolved.SourceDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInterpreterFileWithThePackageBesideItNeedsNoSecondVariable()
    {
        // The development case: a venv's interpreter named outright, and this project's package
        // directory beside it. Until 2026-08-22 the file form skipped that directory and fell
        // straight to the application's bundle, then blamed a directory the interpreter was never
        // "pointed at".
        var other = StageBundle();
        var interpreter = Path.Combine(other, "python", ExecutableName);
        var beside = Path.GetDirectoryName(interpreter)!;
        Directory.CreateDirectory(Path.Combine(beside, "uindosill_engines"));
        Environment.SetEnvironmentVariable(PythonRuntime.InterpreterVariable, interpreter);

        var resolved = PythonRuntime.Resolve(StageNothing(), StageNothing());

        Assert.Equal(interpreter, resolved.Interpreter);
        Assert.Equal(beside, resolved.PackageRoot);
        Assert.True(resolved.InterpreterOverridden);
        Assert.False(resolved.PackagesOverridden);
    }

    [Fact]
    public void APackagesOnlyOverrideKeepsTheBundledInterpreterAndSaysSo()
    {
        // Only the package root is named, so the interpreter is still the bundle's — and the
        // description says so rather than naming a variable nobody set.
        var bundle = StageBundle();
        var other = StageBundle();
        Environment.SetEnvironmentVariable(PythonRuntime.PackagesVariable, Path.Combine(other, "python"));

        var resolved = PythonRuntime.Resolve(bundle, StageNothing());

        Assert.Equal(Path.Combine(bundle, "python", ExecutableName), resolved.Interpreter);
        Assert.Equal(Path.Combine(other, "python"), resolved.PackageRoot);
        Assert.True(resolved.PackagesOverridden);
        Assert.False(resolved.InterpreterOverridden);
        Assert.Contains(PythonRuntime.PackagesVariable, resolved.SourceDescription, StringComparison.Ordinal);
        Assert.Contains("bundled", resolved.SourceDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void APackagesOnlyOverrideWithNoBundleBlamesTheBundleAndNotAVariableNobodySet()
    {
        var other = StageBundle();
        Environment.SetEnvironmentVariable(PythonRuntime.PackagesVariable, Path.Combine(other, "python"));

        var failure = Assert.Throws<PythonSidecarException>(
            () => PythonRuntime.Resolve(StageNothing(), StageNothing()));

        Assert.Contains(PythonRuntime.PackagesVariable, failure.Message, StringComparison.Ordinal);
        Assert.Contains("bundle beside the application", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain($"{PythonRuntime.InterpreterVariable} points at", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveReportsTheReasonInsteadOfThrowingIt()
    {
        // What the window needs: a checkbox cannot be drawn out of an exception, and a missing
        // bundle is a reason to disable an opt-in with the reason beside it rather than to crash a
        // binding getter.
        Assert.False(PythonRuntime.TryResolve(
            out var resolution, out var reason, StageNothing(), StageNothing()));

        Assert.Null(resolution);
        Assert.Contains("The bundled Python is not at", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveSaysNothingWhenThereIsNothingToSay()
    {
        Assert.True(PythonRuntime.TryResolve(
            out var resolution, out var reason, StageBundle(), StageNothing()));

        Assert.NotNull(resolution);
        Assert.Null(reason);
    }

    // ---- The CUDA pack: an overlay put ahead of the bundle, and absent on every shipped install --

    [Fact]
    public void WithoutAPackTheResolutionSaysSoRatherThanFailing()
    {
        // Null is the shipped answer. The bundle pins the CPU torch build, so this is what every
        // installed copy resolves to, and nothing about it is an error.
        var resolved = PythonRuntime.Resolve(StageBundle(), StageNothing());

        Assert.Null(resolved.CudaPackRoot);
    }

    [Fact]
    public void ADownloadedPackIsFoundBesideTheDownloadedBundle()
    {
        var userData = StageCudaPack(StageNothing());

        var resolved = PythonRuntime.Resolve(StageBundle(), userData);

        Assert.Equal(
            Path.Combine(userData, PythonRuntime.CudaPackDirectoryName),
            resolved.CudaPackRoot);
    }

    [Fact]
    public void TheDownloadedPackWinsOverOneBesideTheApplication()
    {
        // **The opposite of the bundle's order, and deliberate.** The pack cannot ship inside the
        // installer — it is 2.8 GB against a 2 GiB asset limit the win-cuda channel is already
        // within 24 MB of — so an <app>/python-cuda is something a hand put there, and a download
        // must not be shadowed by it.
        var application = StageCudaPack(StageBundle());
        var userData = StageCudaPack(StageNothing());

        var resolved = PythonRuntime.Resolve(application, userData);

        Assert.Equal(
            Path.Combine(userData, PythonRuntime.CudaPackDirectoryName),
            resolved.CudaPackRoot);
    }

    [Fact]
    public void APackBesideTheApplicationIsFoundWhenNothingWasDownloaded()
    {
        var application = StageCudaPack(StageBundle());

        var resolved = PythonRuntime.Resolve(application, StageNothing());

        Assert.Equal(
            Path.Combine(application, PythonRuntime.CudaPackDirectoryName),
            resolved.CudaPackRoot);
    }

    [Fact]
    public void TheVariableNamesTheDirectoryItself()
    {
        // Unlike the two places, this names the pack rather than a directory holding one: it is the
        // development override, and a developer points it at what they built.
        var built = TestTemp.NewDirectory("uindosill-pack");
        Directory.CreateDirectory(Path.Combine(built, "torch"));
        File.WriteAllText(Path.Combine(built, "torch", "__init__.py"), "# not really torch");
        Environment.SetEnvironmentVariable(PythonRuntime.CudaPackVariable, built);

        var resolved = PythonRuntime.Resolve(StageBundle(), StageCudaPack(StageNothing()));

        Assert.Equal(built, resolved.CudaPackRoot);
    }

    [Fact]
    public void ADirectoryWithoutTorchIsNotAPack()
    {
        // An interrupted unpack, or a directory that happens to have the name. It is ignored rather
        // than reported: the pack accelerates something that already works, so the honest failure is
        // a diariser that runs on the CPU — which is what a null here produces — and not a refusal.
        var userData = StageNothing();
        Directory.CreateDirectory(Path.Combine(userData, PythonRuntime.CudaPackDirectoryName));

        var resolved = PythonRuntime.Resolve(StageBundle(), userData);

        Assert.Null(resolved.CudaPackRoot);
    }

    [Fact]
    public void ThePackIsFoundEvenWhenTheInterpreterWasNamedByAVariable()
    {
        // The two are orthogonal: pointing UINDOSILL_PYTHON at a venv is the development case, and a
        // developer with a pack wants it found too. This was the bug the `with` expression in
        // Resolve exists to prevent — the environment branch returned early and carried no pack.
        var bundle = StageBundle();
        var userData = StageCudaPack(StageNothing());
        Environment.SetEnvironmentVariable(
            PythonRuntime.InterpreterVariable, Path.Combine(bundle, "python"));

        var resolved = PythonRuntime.Resolve(bundle, userData);

        Assert.True(resolved.Overridden);
        Assert.Equal(
            Path.Combine(userData, PythonRuntime.CudaPackDirectoryName),
            resolved.CudaPackRoot);
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
