using Parakeet.Core.Models;

namespace Parakeet.Cli;

/// <summary>
/// Everything a command needs from its environment, injected so the whole CLI is testable
/// without touching the real console, the real model store or the network.
/// </summary>
internal sealed class CliContext
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    public required IModelStore Store { get; init; }

    public required ModelCatalog Catalog { get; init; }

    /// <summary>False when output is redirected, so progress is not written to a pipe.</summary>
    public bool Interactive { get; init; }

    public static CliContext CreateDefault() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        Store = new LocalModelStore(),
        Catalog = ModelCatalog.Default,
        Interactive = !Console.IsOutputRedirected && !Console.IsErrorRedirected,
    };

    public void WriteLine(string text = "") => Out.WriteLine(text);

    public void WriteError(string text) => Error.WriteLine(text);
}

internal static class ExitCodes
{
    public const int Success = 0;
    public const int UsageError = 2;
    public const int RuntimeError = 1;

    /// <summary>
    /// At least one file in a batch failed or was cancelled, or was written without a pass it asked
    /// for — speaker labels, the English version — while the batch as a whole produced output. A
    /// batch in which nothing finished is <see cref="RuntimeError"/>, cancelled or not.
    /// </summary>
    public const int PartialFailure = 3;
}
