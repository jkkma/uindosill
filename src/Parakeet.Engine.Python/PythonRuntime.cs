using System.Runtime.InteropServices;
using Parakeet.Core.Models;

namespace Parakeet.Engine.Python;

/// <summary>
/// Where the interpreter and the <c>uindosill_engines</c> package are, and what to say when they
/// are not there.
/// </summary>
/// <remarks>
/// <para>
/// The shipped answer is a Python bundled beside the application, so a user installs nothing and
/// no system Python is consulted, found, or blamed. Deliberately <b>not</b> PATH: picking up
/// whatever <c>python</c> happens to resolve to is how a working install turns into a support
/// thread about somebody's conda environment.
/// </para>
/// <para>
/// <b>Three places, in this order, and the order is the decision of 2026-08-21.</b> The installer
/// puts the bundle inside the desktop application's publish; the CLI ships as a zip with no
/// interpreter in it, and the bundle is a third download beside them both. So a bundle can be in
/// one of three places and this looks in all of them: <see cref="InterpreterVariable"/> first
/// because an explicit answer must win, then <c>&lt;app&gt;/python</c>, then <c>python</c> under
/// <see cref="UserDataPaths.RootDirectory"/> — which is where the downloaded bundle is meant to be
/// unpacked, and is already where the model weights live, so a user who has found one directory has
/// found both.
/// </para>
/// <para>
/// <see cref="InterpreterVariable"/> takes <b>either</b> a bundle directory or an interpreter file.
/// A directory answers both halves at once and is what a downloaded bundle is; a file is the
/// development case, where the interpreter is a venv's and the package root has to be named
/// separately with <see cref="PackagesVariable"/>. One variable rather than two because the thing a
/// user is pointing at is one thing, and because a shipping mechanism that is also the development
/// override is one mechanism to document and one to keep working.
/// </para>
/// <para>
/// Which of the three answered is carried on the result rather than inferred by the caller. A figure
/// taken against an unknown interpreter is a figure that cannot be reproduced, and until 2026-08-21
/// this type computed that fact and nothing read it.
/// </para>
/// </remarks>
public static class PythonRuntime
{
    /// <summary>
    /// A bundle directory, or the full path to an interpreter to use instead of the bundled one.
    /// </summary>
    public const string InterpreterVariable = "UINDOSILL_PYTHON";

    /// <summary>Directory containing the <c>uindosill_engines</c> package, if not beside the app.</summary>
    public const string PackagesVariable = "UINDOSILL_PYTHON_PACKAGES";

    /// <summary>The directory name a bundle takes, in every one of the three places.</summary>
    public const string BundleDirectoryName = "python";

    /// <summary>A directory of CUDA-built packages to put ahead of the bundle's own.</summary>
    public const string CudaPackVariable = "UINDOSILL_PYTHON_CUDA";

    /// <summary>The directory name the CUDA pack takes where it is not named by a variable.</summary>
    public const string CudaPackDirectoryName = "python-cuda";

    /// <summary>
    /// What a directory must contain to be a CUDA pack rather than an empty or half-unpacked one.
    /// </summary>
    /// <remarks>
    /// <b>The pack is an overlay, not a bundle</b>, so it cannot be recognised by an interpreter the
    /// way <see cref="BundleDirectoryName"/> is. Measured 2026-08-28 on this project's own pins, the
    /// entire difference between a CPU and a CUDA install of the bundle is three packages — `torch`
    /// at 2778 MB against 490 MB, `torchcodec` at 38 MB against 23 MB, and `torchaudio` at 9 MB
    /// against 2 MB — and on Windows there are no separate `nvidia_*` distributions at all, the CUDA
    /// libraries living inside `torch/lib`. So the pack is those three directories and their
    /// dist-info, put ahead of the bundle on <c>PYTHONPATH</c>, which shadows the CPU build without
    /// touching it: 2.8 GB rather than the 4 GB a second whole bundle would cost, and no change to
    /// the three-place resolution above.
    /// <para>
    /// `torch` alone is checked because it is the package that carries the CUDA libraries and the
    /// one whose absence makes the pack pointless; a pack missing one of the other two is a
    /// half-unpacked archive, which the digests on the download are the right place to catch.
    /// </para>
    /// </remarks>
    public static readonly string[] CudaPackMarker = ["torch", "__init__.py"];

    /// <summary>Which of the three places answered.</summary>
    public enum BundleSource
    {
        /// <summary>Named by <see cref="InterpreterVariable"/> or <see cref="PackagesVariable"/>.</summary>
        Environment,

        /// <summary>The bundle the installer puts beside the application.</summary>
        Application,

        /// <summary>A downloaded bundle unpacked under <see cref="UserDataPaths.RootDirectory"/>.</summary>
        UserData,
    }

    /// <summary>The interpreter, the package root, and which of the three places they came from.</summary>
    public sealed record Resolution
    {
        public required string Interpreter { get; init; }

        public required string PackageRoot { get; init; }

        /// <summary>Which of the three places answered.</summary>
        public required BundleSource Source { get; init; }

        /// <summary>True when either half came from an environment variable rather than a bundle.</summary>
        public bool Overridden => Source == BundleSource.Environment;

        /// <summary>True when the interpreter came from <see cref="InterpreterVariable"/>.</summary>
        public bool InterpreterOverridden { get; init; }

        /// <summary>True when the package root came from <see cref="PackagesVariable"/>.</summary>
        public bool PackagesOverridden { get; init; }

        /// <summary>
        /// A directory of CUDA-built packages to search before <see cref="PackageRoot"/>, or null
        /// where none is installed — which is every machine that has not downloaded one.
        /// </summary>
        /// <remarks>
        /// <b>Null is the shipped answer and not a failure.</b> The bundle pins the CPU torch build,
        /// so the diariser's <c>auto</c> elects the CPU on an installed copy; this is the one thing
        /// that changes that, and it is opt-in by download. Resolution never throws for its absence
        /// and never mentions it in the message when the bundle itself is missing, because a user
        /// without a bundle has a different problem and two problems in one message is neither.
        /// </remarks>
        public string? CudaPackRoot { get; init; }

        /// <summary>
        /// One phrase naming where this came from, for a run to report rather than a caller to guess.
        /// </summary>
        /// <remarks>
        /// Which half was overridden matters to the phrase: a run whose package root alone was named
        /// still ran the bundled interpreter, and saying it was "named by UINDOSILL_PYTHON" — a
        /// variable nobody set — would send whoever reads the report looking for the wrong thing.
        /// </remarks>
        public string SourceDescription => Source switch
        {
            BundleSource.Environment => (InterpreterOverridden, PackagesOverridden) switch
            {
                (true, true) => $"named by {InterpreterVariable} and {PackagesVariable}",
                (false, true) => $"bundled beside the application, with the package root named by {PackagesVariable}",
                _ => $"named by {InterpreterVariable}",
            },
            BundleSource.Application => "bundled beside the application",
            BundleSource.UserData => "downloaded, under " + UserDataPaths.DirectoryName,
            _ => "unknown",
        };
    }

    /// <summary>
    /// Resolves both halves, or reports why not. False is an answer rather than a failure.
    /// </summary>
    /// <remarks>
    /// The window needs this shape and the command line does not. A missing bundled Python is a
    /// reason to disable the speaker and English opt-ins <i>with the reason beside them</i> — the
    /// same treatment a model that has not been downloaded gets — and a checkbox cannot be drawn
    /// out of an exception. The command line meets the same situation as a command that fails, so
    /// it uses <see cref="Resolve"/> and gets the message thrown.
    /// </remarks>
    public static bool TryResolve(
        out Resolution? resolution,
        out string? reason,
        string? baseDirectory = null,
        string? userDataDirectory = null)
    {
        try
        {
            resolution = Resolve(baseDirectory, userDataDirectory);
            reason = null;
            return true;
        }
        catch (PythonSidecarException exception)
        {
            resolution = null;
            reason = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Resolves all three places, or throws with the reason. <paramref name="baseDirectory"/>
    /// defaults to the application's own directory and <paramref name="userDataDirectory"/> to
    /// <see cref="UserDataPaths.RootDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Both are parameters rather than reads so that a test is not a question about the machine it
    /// runs on. A resolver that consults <c>%LOCALAPPDATA%</c> unconditionally passes or fails
    /// depending on whether the person running the suite happens to have downloaded a bundle, which
    /// is the one thing this project's tests are not allowed to depend on.
    /// </remarks>
    public static Resolution Resolve(string? baseDirectory = null, string? userDataDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;

        // Resolved before the branch and carried onto whichever answer wins, because the pack is
        // orthogonal to which of the three places held the bundle: a developer pointing
        // UINDOSILL_PYTHON at a venv wants the pack found too, and a downloaded bundle and a
        // downloaded pack land in the same directory as each other.
        var cudaPack = ResolveCudaPack(baseDirectory, userDataDirectory);

        var interpreterOverride = Environment.GetEnvironmentVariable(InterpreterVariable);
        var packagesOverride = Environment.GetEnvironmentVariable(PackagesVariable);

        if (interpreterOverride is { Length: > 0 } || packagesOverride is { Length: > 0 })
        {
            return FromEnvironment(interpreterOverride, packagesOverride, baseDirectory)
                with { CudaPackRoot = cudaPack };
        }

        // Beside the application first, then the downloaded bundle. Both are checked before either
        // is complained about, so the message can name every place that was looked in rather than
        // only the first — a user who unpacked the download somewhere else needs to be told where
        // it was expected, not told again that the installer's copy is missing.
        var applicationBundle = Path.Combine(baseDirectory, BundleDirectoryName);
        var userDataBundle = Path.Combine(
            userDataDirectory ?? UserDataPaths.RootDirectory(), BundleDirectoryName);

        // A directory holding an interpreter but no package is half a bundle, and it is worth its
        // own message: an interrupted unzip and a missing download send a reader in different
        // directions. Collected rather than thrown from inside the loop, because the second place
        // may still hold a whole one.
        var halves = new List<string>();

        foreach (var (directory, source) in new[]
                 {
                     (applicationBundle, BundleSource.Application),
                     (userDataBundle, BundleSource.UserData),
                 })
        {
            var interpreter = Path.Combine(directory, ExecutableName);
            if (!File.Exists(interpreter))
            {
                continue;
            }

            if (HasEngines(directory))
            {
                return new Resolution
                {
                    Interpreter = interpreter,
                    PackageRoot = directory,
                    Source = source,
                    CudaPackRoot = cudaPack,
                };
            }

            halves.Add(directory);
        }

        if (halves.Count > 0)
        {
            throw new PythonSidecarException(
                $"No 'uindosill_engines' package under {string.Join(" or ", halves)}, which holds " +
                $"{ExecutableName} and so is half a bundle rather than none. An interrupted unpack " +
                $"looks like this; so does a bundle assembled without its source. Set " +
                $"{PackagesVariable} if the package lives somewhere else.");
        }

        throw new PythonSidecarException(
            "The bundled Python is not at " + applicationBundle + " or " + userDataBundle + ". " +
            "Speaker labelling and translation run in one, so neither is available until it is " +
            "there. The desktop installer carries a bundle and the command-line zip does not, so " +
            $"unpack the separate bundle download at the second path, or set {InterpreterVariable} " +
            "to a bundle directory, or to an interpreter with this project's requirements installed.");
    }

    /// <summary>
    /// The environment's answer, where <see cref="InterpreterVariable"/> may name a bundle
    /// directory or an interpreter file.
    /// </summary>
    /// <remarks>
    /// A directory is the downloaded bundle and answers both halves. A file is the development case
    /// and answers only one: the package root then falls back to <see cref="PackagesVariable"/> and
    /// finally to the application's own bundle, which is the behaviour that existed before a
    /// directory was accepted and is what a venv interpreter with the repository's <c>python/</c>
    /// directory relies on.
    /// </remarks>
    private static Resolution FromEnvironment(
        string? interpreterOverride, string? packagesOverride, string baseDirectory)
    {
        string interpreter;
        string packageRoot;

        if (interpreterOverride is { Length: > 0 } && Directory.Exists(interpreterOverride))
        {
            // The directory form: a bundle, answering both halves unless the package root is named.
            interpreter = Path.Combine(interpreterOverride, ExecutableName);
            packageRoot = packagesOverride is { Length: > 0 } ? packagesOverride : interpreterOverride;

            if (!File.Exists(interpreter))
            {
                throw new PythonSidecarException(
                    $"{InterpreterVariable} names the directory {interpreterOverride}, which is read " +
                    $"as a bundle: but there is no {ExecutableName} in it. A bundle holds the " +
                    "interpreter at its root; point the variable at an interpreter file instead if " +
                    "that is what it is.");
            }

            if (!HasEngines(packageRoot))
            {
                throw new PythonSidecarException(packagesOverride is { Length: > 0 }
                    ? $"No 'uindosill_engines' package under {packageRoot}, which {PackagesVariable} names."
                    : $"No 'uindosill_engines' package under {packageRoot}, the bundle directory " +
                      $"{InterpreterVariable} names; set {PackagesVariable} if it lives somewhere else.");
            }
        }
        else if (interpreterOverride is { Length: > 0 })
        {
            // The file form. The interpreter is named outright; the package root is named too, or
            // is beside the interpreter — the repository's `python/` next to a venv's interpreter,
            // the development case — or is the application's own bundle, in that order. Until
            // 2026-08-22 the middle one was not tried, and the message then blamed a directory the
            // interpreter was never "pointed at".
            interpreter = interpreterOverride;
            if (!File.Exists(interpreter))
            {
                throw new PythonSidecarException(
                    $"{InterpreterVariable} points at {interpreter}, which is neither a file nor a " +
                    "directory holding one.");
            }

            var beside = Path.GetDirectoryName(interpreter) ?? baseDirectory;
            packageRoot = packagesOverride is { Length: > 0 } ? packagesOverride
                : HasEngines(beside) ? beside
                : Path.Combine(baseDirectory, BundleDirectoryName);

            if (!HasEngines(packageRoot))
            {
                throw new PythonSidecarException(packagesOverride is { Length: > 0 }
                    ? $"No 'uindosill_engines' package under {packageRoot}, which {PackagesVariable} names."
                    : $"No 'uindosill_engines' package beside {interpreter} or under {packageRoot}; set " +
                      $"{PackagesVariable} to the directory that holds it.");
            }
        }
        else
        {
            // Only the package root is named, so the interpreter is still the bundle's — and a
            // bundle that is not there is the bundle's absence, not the fault of a variable nobody
            // set. Until 2026-08-22 this blamed UINDOSILL_PYTHON.
            interpreter = Path.Combine(baseDirectory, BundleDirectoryName, ExecutableName);
            packageRoot = packagesOverride!;

            if (!File.Exists(interpreter))
            {
                throw new PythonSidecarException(
                    $"{PackagesVariable} names the package root {packageRoot}, and the interpreter still comes " +
                    $"from the bundle beside the application: but there is no {ExecutableName} at {interpreter}. " +
                    $"Set {InterpreterVariable} as well if the interpreter lives somewhere else.");
            }

            if (!HasEngines(packageRoot))
            {
                throw new PythonSidecarException(
                    $"No 'uindosill_engines' package under {packageRoot}, which {PackagesVariable} names.");
            }
        }

        return new Resolution
        {
            Interpreter = interpreter,
            PackageRoot = packageRoot,
            Source = BundleSource.Environment,
            InterpreterOverridden = interpreterOverride is { Length: > 0 },
            PackagesOverridden = packagesOverride is { Length: > 0 },
        };
    }

    private static bool HasEngines(string packageRoot) =>
        Directory.Exists(Path.Combine(packageRoot, "uindosill_engines"));

    /// <summary>
    /// The CUDA pack directory, or null where none is installed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two places and a variable, mirroring the bundle's three</b> — <see cref="CudaPackVariable"/>
    /// first so an explicit answer wins, then <c>&lt;user data&gt;/python-cuda</c>, then
    /// <c>&lt;app&gt;/python-cuda</c>. The user-data copy is checked <i>before</i> the application's,
    /// which is the opposite of the bundle's order and deliberate: the pack cannot ship inside the
    /// installer — the win-cuda channel's Setup.exe was measured at 1,976,256,205 bytes against
    /// GitHub's 2 GiB asset limit, with the pack 2.8 GB on its own — so an <c>&lt;app&gt;/python-cuda</c>
    /// can only be something a user or a build put there by hand, and a download must not be shadowed
    /// by it.
    /// </para>
    /// <para>
    /// <b>A directory that exists but does not hold torch is ignored rather than reported.</b> The
    /// pack is an accelerator for something that already works; the honest failure for a broken one
    /// is a diariser that runs on the CPU, which is what happens, and not a load that refuses.
    /// A user who downloaded a pack and is not getting CUDA is told by the run's own provenance —
    /// the elected device is reported — rather than by a resolver that has to guess whether the
    /// directory was meant to be a pack at all.
    /// </para>
    /// </remarks>
    private static string? ResolveCudaPack(string baseDirectory, string? userDataDirectory)
    {
        var named = Environment.GetEnvironmentVariable(CudaPackVariable);
        var candidates = named is { Length: > 0 }
            ? [named]
            : new[]
            {
                Path.Combine(userDataDirectory ?? UserDataPaths.RootDirectory(), CudaPackDirectoryName),
                Path.Combine(baseDirectory, CudaPackDirectoryName),
            };

        return candidates.FirstOrDefault(IsCudaPack);
    }

    /// <summary>Whether a directory holds the package whose absence makes a pack pointless.</summary>
    public static bool IsCudaPack(string directory) =>
        Directory.Exists(directory)
        && File.Exists(Path.Combine([directory, .. CudaPackMarker]));

    private static string ExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python.exe" : "bin/python3";
}

/// <summary>
/// A failure of the sidecar itself — it could not be started, it died, or it broke the protocol.
/// </summary>
/// <remarks>
/// Distinct from a failure the sidecar <i>reported</i>, which arrives as an error message and
/// becomes an ordinary exception about that file. The difference is what a batch does next: a file
/// that could not be read is one file, and a dead interpreter is every remaining file.
/// </remarks>
public sealed class PythonSidecarException : Exception
{
    public PythonSidecarException(string message) : base(message)
    {
    }

    public PythonSidecarException(string message, Exception inner) : base(message, inner)
    {
    }
}
