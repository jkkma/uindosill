using System.Runtime.InteropServices;
using System.Text.Json;
using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// A scripted stand-in for the bundled Python, and the resolution that points a real
/// <see cref="PythonSidecar"/> at it.
/// </summary>
/// <remarks>
/// <para>
/// Everything in <c>Parakeet.Engine.Python</c> that is worth testing sits above the process
/// boundary — the handshake, correlating replies by id, progress interleaving with results, what a
/// dead child does to the requests still waiting on it — and none of it needs Python. What it needs
/// is a process that can be told to misbehave, which is <c>tools/FakeSidecar</c>.
/// </para>
/// <para>
/// <b>No weights, no network, no Python, and it runs on Linux.</b> That is the constraint the rest
/// of this suite honours and the reason a .NET stand-in was chosen over a small Python script:
/// <c>scripts/cloud-setup.sh</c> installs the SDK and PowerShell and no interpreter, so a test that
/// needed <c>python3</c> would be a test that skipped itself in CI.
/// </para>
/// <para>
/// The script travels in a temporary directory handed over as the package root, which the host puts
/// in the child's <c>PYTHONPATH</c>. That makes it a private channel between one test and one
/// child: no shared environment variable, so nothing races and nothing has to be serialised.
/// </para>
/// </remarks>
internal sealed class FakeSidecarProcess : IDisposable
{
    /// <summary>A reply that satisfies the handshake, so a test that is about something else can ignore it.</summary>
    public const string Handshake =
        """{"id":{id},"type":"result","protocol":1,"python":"3.12.10","engines":["diariser","translator"]}""";

    private readonly string _directory;

    private FakeSidecarProcess(string directory) => _directory = directory;

    /// <summary>Writes a script and returns the resolution that runs it.</summary>
    public static FakeSidecarProcess Scripted(object script)
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-fake-sidecar").FullName;

        // The one thing PythonRuntime.Resolve insists on that this bypasses. Created anyway so the
        // staged directory is the shape the real one is, and so a test that decides to go through
        // Resolve does not have to arrange it separately.
        Directory.CreateDirectory(Path.Combine(directory, "uindosill_engines"));

        File.WriteAllText(
            Path.Combine(directory, "script.json"),
            JsonSerializer.Serialize(script, new JsonSerializerOptions { WriteIndented = true }));

        return new FakeSidecarProcess(directory);
    }

    /// <summary>
    /// The resolution to hand a <see cref="PythonSidecar"/>.
    /// </summary>
    /// <remarks>
    /// Built directly rather than through <see cref="PythonRuntime.Resolve"/>, which is what makes
    /// this possible at all: the record is public with <c>required</c> initialisers, so a test can
    /// name any executable without arranging a bundle on disk. <c>Overridden</c> is true because it
    /// is — this is not the bundled interpreter.
    /// </remarks>
    public PythonRuntime.Resolution Resolution => new()
    {
        Interpreter = ExecutablePath,
        PackageRoot = _directory,
        Overridden = true,
    };

    /// <summary>A started sidecar, with the handshake already done.</summary>
    public static async Task<(FakeSidecarProcess Fake, PythonSidecar Sidecar)> StartAsync(object script)
    {
        var fake = Scripted(script);
        var sidecar = new PythonSidecar(fake.Resolution);
        await sidecar.StartAsync();
        return (fake, sidecar);
    }

    /// <summary>
    /// Where the build put the stand-in. Copied beside the tests by <c>CopyFakeSidecar</c>.
    /// </summary>
    private static string ExecutablePath
    {
        get
        {
            var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "FakeSidecar.exe" : "FakeSidecar";
            var path = Path.Combine(AppContext.BaseDirectory, "fake-sidecar", name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"The scripted stand-in is not at {path}. It is built by tools/FakeSidecar and copied here by " +
                    "this project's CopyFakeSidecar target; a missing one means the target did not run, not that " +
                    "the test is wrong.",
                    path);
            }

            return path;
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the run is untidy, not a failure worth failing a test for.
        }
    }
}
