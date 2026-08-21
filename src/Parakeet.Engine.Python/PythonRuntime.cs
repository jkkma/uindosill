using System.Runtime.InteropServices;

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
/// The two environment variables exist for development and for the measurement harnesses, which
/// need to drive the same protocol out of a venv that is not the bundle. They are read before the
/// bundle so a developer can override without moving files around, and a run that used one says so
/// — a figure taken against an unknown interpreter is a figure that cannot be reproduced.
/// </para>
/// </remarks>
public static class PythonRuntime
{
    /// <summary>Full path to an interpreter to use instead of the bundled one.</summary>
    public const string InterpreterVariable = "UINDOSILL_PYTHON";

    /// <summary>Directory containing the <c>uindosill_engines</c> package, if not beside the app.</summary>
    public const string PackagesVariable = "UINDOSILL_PYTHON_PACKAGES";

    /// <summary>The interpreter, the package root, and which of them came from an override.</summary>
    public sealed record Resolution
    {
        public required string Interpreter { get; init; }

        public required string PackageRoot { get; init; }

        /// <summary>True when either half came from an environment variable rather than the bundle.</summary>
        public required bool Overridden { get; init; }
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
    public static bool TryResolve(out Resolution? resolution, out string? reason, string? baseDirectory = null)
    {
        try
        {
            resolution = Resolve(baseDirectory);
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
    /// Resolves both halves, or throws with the reason. <paramref name="baseDirectory"/> defaults
    /// to the application's own directory.
    /// </summary>
    public static Resolution Resolve(string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;

        var interpreterOverride = Environment.GetEnvironmentVariable(InterpreterVariable);
        var packagesOverride = Environment.GetEnvironmentVariable(PackagesVariable);

        var interpreter = interpreterOverride is { Length: > 0 }
            ? interpreterOverride
            : Path.Combine(baseDirectory, "python", ExecutableName);

        var packageRoot = packagesOverride is { Length: > 0 }
            ? packagesOverride
            : Path.Combine(baseDirectory, "python");

        if (!File.Exists(interpreter))
        {
            throw new PythonSidecarException(
                interpreterOverride is { Length: > 0 }
                    ? $"{InterpreterVariable} points at {interpreter}, which is not a file."
                    : $"The bundled Python is not at {interpreter}. Speaker labelling and translation " +
                      $"run in it, so neither is available until it is there. Set {InterpreterVariable} " +
                      "to an interpreter with this project's requirements installed to use another one.");
        }

        if (!Directory.Exists(Path.Combine(packageRoot, "uindosill_engines")))
        {
            throw new PythonSidecarException(
                $"No 'uindosill_engines' package under {packageRoot}. That directory is what the " +
                $"interpreter is pointed at; set {PackagesVariable} if it lives somewhere else.");
        }

        return new Resolution
        {
            Interpreter = interpreter,
            PackageRoot = packageRoot,
            Overridden = interpreterOverride is { Length: > 0 } || packagesOverride is { Length: > 0 },
        };
    }

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
