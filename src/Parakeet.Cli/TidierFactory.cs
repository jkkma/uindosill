using Parakeet.Core.Models;
using Parakeet.Core.Tidying;
using Parakeet.Core.Transcription;
using Parakeet.Engine.LlamaServer;

namespace Parakeet.Cli;

internal sealed record TidierRequest
{
    /// <summary>Use the canned tidier: real stage, real contract, no model.</summary>
    public bool Fake { get; init; }

    /// <summary>A <c>.gguf</c> to serve directly instead of the catalogue's tidying entry.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Which vendored drop, or null for the best present.</summary>
    public ComputeBackend? Backend { get; init; }

    /// <summary>Where the drops live, or null for beside the executable.</summary>
    public string? ServerRoot { get; init; }

    /// <summary>How many lines in flight; what the child's slot count is set to.</summary>
    public int Concurrency { get; init; } = TidyOptions.Default.Concurrency;
}

/// <summary>
/// Resolves the tidier behind <c>--tidy</c>, on <c>TranslatorFactory</c>'s terms: every way the
/// resolution can fail wants its own sentence, and it wants it before 1.34 GiB of recogniser
/// weights have loaded rather than after a three-hour decode.
/// </summary>
internal static class TidierFactory
{
    /// <summary>
    /// Refuses now what would otherwise be refused after the recogniser loaded: no drop, no
    /// entry, an entry not installed, a path that is not a file.
    /// </summary>
    public static void Resolve(CliContext context, TidierRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Fake)
        {
            return;
        }

        ResolveServer(request);
        ResolveModel(context, request);
    }

    /// <summary>Builds the tidier and loads it, so the child is healthy before any file is decoded.</summary>
    public static async Task<ITranscriptTidier> CreateAsync(CliContext context, TidierRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Fake)
        {
            context.WriteError("Using the canned tidier: the stage and the contract are real, the rewrites are not a model's.");
            return new FakeTranscriptTidier();
        }

        var install = ResolveServer(request);
        var (modelPath, _) = ResolveModel(context, request);

        ITranscriptTidier tidier = LlamaServerTranscriptTidier.Create(
            modelPath, install.Backend, request.ServerRoot, request.Concurrency);

        try
        {
            await tidier.LoadAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tidier.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        context.WriteError(
            $"Tidying with {tidier.Capabilities.ModelId} on {install.Backend.ToString().ToLowerInvariant()}, " +
            $"{request.Concurrency} lines in flight beside the recogniser.");
        return tidier;
    }

    private static LlamaServerInstall ResolveServer(TidierRequest request)
    {
        if (LlamaServerLocator.TryFind(request.Backend, request.ServerRoot) is { } install)
        {
            return install;
        }

        var where = request.ServerRoot is { } root ? $" under {root}." : " beside this executable.";
        var which = request.Backend is { } wanted
            ? $"No {LlamaServerLocator.BackendDirectoryName(wanted)} llama-server drop is vendored"
            : "No llama-server drop is vendored";

        throw new CliUsageException(
            which + where + " Run scripts/vendor-llm-natives.ps1, or point --tidy-server-root at a native/win-x64/llm directory.");
    }

    /// <summary>The weights to serve — never the drafting head, which is paired by the engine.</summary>
    public static (string ModelPath, ModelDescriptor? Descriptor) ResolveModel(CliContext context, TidierRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.ModelPath is { Length: > 0 } explicitPath)
        {
            if (!File.Exists(explicitPath))
            {
                throw new CliUsageException(
                    $"Tidying model not found: {explicitPath}. --tidy-model-path takes a .gguf file.");
            }

            if (DraftModelLocator.IsDraftHead(explicitPath))
            {
                throw new CliUsageException(
                    $"{explicitPath} is a drafting head, not a model: it is paired with the model beside it and cannot tidy anything on its own.");
            }

            return (explicitPath, null);
        }

        var descriptor = context.Catalog.TidyingModels.Count switch
        {
            0 => throw new CliUsageException("The model catalogue has no tidying model."),
            1 => context.Catalog.TidyingModels[0],
            _ => throw new CliUsageException(
                "The catalogue has more than one tidying model, and --tidy-model-path is the way to name one. Candidates: " +
                string.Join(", ", context.Catalog.TidyingModels.Select(m => m.Id))),
        };

        var directory = context.Store.PathFor(descriptor);
        if (!context.Store.IsInstalled(descriptor))
        {
            throw new CliUsageException(
                $"The tidying model '{descriptor.Id}' is not installed. Run " +
                $"'uindosill models download {descriptor.Id}' first (it would be at {directory}).");
        }

        var weights = descriptor.Files.FirstOrDefault(file => !DraftModelLocator.IsDraftHead(file.FileName))
            ?? throw new CliUsageException($"The tidying entry '{descriptor.Id}' names no weights, only a drafting head.");

        return (Path.Combine(directory, weights.FileName), descriptor);
    }
}
