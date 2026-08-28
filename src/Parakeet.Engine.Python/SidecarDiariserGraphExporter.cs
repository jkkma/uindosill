using System.Text.Json;

namespace Parakeet.Engine.Python;

/// <summary>
/// What the exported graphs are, where they live, and whether a model directory has them.
/// </summary>
/// <remarks>
/// <b>The names are duplicated in two other places on purpose, and each copy earns its keep.</b>
/// The sidecar's <c>onnx_export</c> writes them, and <c>LabellerFactory</c> checks for them before
/// the CLI starts a subprocess. This copy is the one the application reads, so that the Settings
/// window can offer the GPU row only when it would work. A shared constant across a process
/// boundary and a language boundary is not available; a test asserting the three agree is, and is
/// cheaper than the coupling.
/// </remarks>
public static class DiariserGraphs
{
    /// <summary>The subdirectory of the model directory the graphs live in.</summary>
    public const string Subdirectory = "onnx";

    /// <summary>The two graphs an ONNX execution provider needs.</summary>
    public static IReadOnlyList<string> FileNames { get; } = ["segmentation.onnx", "embedding.onnx"];

    /// <summary>Whether <paramref name="modelDirectory"/> has both graphs beside its weights.</summary>
    /// <remarks>
    /// A file-existence check and nothing more. Whether a graph loads is the sidecar's to find out,
    /// and a window that tried to answer it here would have to start a subprocess to redraw a
    /// picker.
    /// </remarks>
    public static bool AreInstalled(string? modelDirectory)
    {
        if (modelDirectory is not { Length: > 0 } directory)
        {
            return false;
        }

        var graphs = Path.Combine(directory, Subdirectory);
        return FileNames.All(name => File.Exists(Path.Combine(graphs, name)));
    }

    /// <summary>The directory the graphs go in for a given model directory.</summary>
    public static string DirectoryFor(string modelDirectory) =>
        Path.Combine(modelDirectory, Subdirectory);
}

/// <summary>
/// Derives the diariser's ONNX graphs through the sidecar, so the GPU route can be turned on from
/// the application instead of from a script.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own sidecar, started and stopped around the one call.</b> The export needs no loaded
/// pipeline — it is a property of the weights on disk — and running it inside the labelling
/// sidecar would hold two copies of the checkpoints while a transcription might be using the first.
/// A separate short-lived child costs a few seconds of interpreter start against a job that takes
/// tens of seconds anyway.
/// </para>
/// <para>
/// <b>Nothing here decides whether to export.</b> That is the caller's, because the answer is a
/// user's: the graphs are 32 MiB of derived artefact that a person who never uses the GPU route
/// does not need.
/// </para>
/// </remarks>
public sealed class SidecarDiariserGraphExporter
{
    private readonly Func<PythonSidecar> _sidecarFactory;

    public SidecarDiariserGraphExporter(Func<PythonSidecar>? sidecarFactory = null) =>
        _sidecarFactory = sidecarFactory ?? (static () => new PythonSidecar(PythonRuntime.Resolve()));

    /// <summary>
    /// Writes both graphs into <paramref name="modelDirectory"/>'s <c>onnx</c> subdirectory and
    /// returns the path they landed in.
    /// </summary>
    /// <param name="modelDirectory">The installed pyannote pipeline directory.</param>
    /// <param name="progress">Steps completed and steps total, as the sidecar reports them.</param>
    /// <param name="ct">
    /// Cancels the wait, not the export: the child is doing a torch trace it cannot be interrupted
    /// in the middle of, so cancelling disposes the sidecar and the partial files are cleaned up
    /// here rather than left to be found later and trusted.
    /// </param>
    public async Task<string> ExportAsync(
        string modelDirectory,
        IProgress<(int Completed, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        if (!Directory.Exists(modelDirectory))
        {
            throw new PythonSidecarException(
                $"The speaker model directory is not at {modelDirectory}.");
        }

        var sidecar = _sidecarFactory();
        await using (sidecar.ConfigureAwait(false))
        {
            try
            {
                await sidecar.StartAsync(ct).ConfigureAwait(false);

                var reply = await sidecar.SendAsync(
                    "exportDiariserGraphs",
                    writer => writer.WriteString("path", modelDirectory),
                    progress,
                    ct).ConfigureAwait(false);

                if (!reply.TryGetProperty("manifest", out var manifest)
                    || manifest.ValueKind != JsonValueKind.Object)
                {
                    throw new PythonSidecarException("The export returned no manifest.");
                }

                var directory = manifest.TryGetProperty("out_dir", out var outDir)
                    && outDir.ValueKind == JsonValueKind.String
                        ? outDir.GetString()!
                        : DiariserGraphs.DirectoryFor(modelDirectory);

                // **Checked here as well as in the child**, because "the op returned" and "the two
                // files a provider needs are on disk" are different claims, and it is this one the
                // picker is about to act on.
                if (!DiariserGraphs.AreInstalled(modelDirectory))
                {
                    throw new PythonSidecarException(
                        $"The export reported success but {directory} does not hold both graphs.");
                }

                return directory;
            }
            catch
            {
                // A half-written graph is worse than none: it passes the existence check the picker
                // uses and fails at session creation, which is far from here.
                CleanUpPartial(modelDirectory);
                throw;
            }
        }
    }

    private static void CleanUpPartial(string modelDirectory)
    {
        if (DiariserGraphs.AreInstalled(modelDirectory))
        {
            return;
        }

        var directory = DiariserGraphs.DirectoryFor(modelDirectory);
        foreach (var name in DiariserGraphs.FileNames)
        {
            try
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Left behind rather than retried: the next export overwrites, and the check above
                // is what stops a partial set being offered in the meantime.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
