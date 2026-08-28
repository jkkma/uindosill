using Parakeet.Engine.Python;

namespace Parakeet.App.Services;

/// <summary>
/// Derives the diariser's ONNX graphs, so the Settings window's graphics option can prepare itself.
/// </summary>
/// <remarks>
/// The same seam <see cref="IAnswerEngineProvider"/> is for the answering tier, for the same
/// reason: the headless window tests drive this path — a person choosing the graphics row — and
/// they must not start a Python or write 32 MiB beside somebody's weights to do it.
/// </remarks>
public interface IDiariserGraphExporter
{
    /// <summary>
    /// Writes both graphs beside the model in <paramref name="modelDirectory"/> and returns the
    /// directory they landed in. Throws when they could not be produced.
    /// </summary>
    Task<string> ExportAsync(
        string modelDirectory,
        IProgress<(int Completed, int Total)>? progress = null,
        CancellationToken ct = default);
}

/// <summary>The real one: the sidecar's exporter behind the application's seam.</summary>
/// <remarks>
/// A thin adapter rather than making <see cref="SidecarDiariserGraphExporter"/> implement the
/// interface directly, because that type lives in the engine assembly and the interface is the
/// application's. The engine layer does not know this application exists, which is the rule every
/// other engine seam here follows.
/// </remarks>
public sealed class SidecarDiariserGraphExporterAdapter : IDiariserGraphExporter
{
    private readonly SidecarDiariserGraphExporter _inner = new();

    public Task<string> ExportAsync(
        string modelDirectory,
        IProgress<(int Completed, int Total)>? progress = null,
        CancellationToken ct = default) =>
        _inner.ExportAsync(modelDirectory, progress, ct);
}
