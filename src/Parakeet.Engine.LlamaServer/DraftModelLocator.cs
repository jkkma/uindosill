namespace Parakeet.Engine.LlamaServer;

/// <summary>
/// Pairs a model with the multi-token-prediction head that belongs to it, by name alone.
/// </summary>
/// <remarks>
/// <para>
/// The publisher's convention is what this reads: the head for
/// <c>gemma-4-26B-A4B-it-UD-IQ4_XS.gguf</c> ships as <c>mtp-gemma-4-26B-A4B-it.gguf</c>, so the
/// head names the model family and the model adds a quantisation to it. Stripping <c>mtp-</c> and
/// the extension leaves a stem that the model's own filename begins with, and that prefix test is
/// the whole rule.
/// </para>
/// <para>
/// <b>Why a name and not a GGUF metadata check:</b> the pairing has to be decidable before either
/// file is opened, because the answer decides an argument on the child's command line. A wrong
/// pair is not silent — llama-server refuses to load a draft whose vocabulary does not match —
/// so the failure mode this rule has to avoid is a false <i>positive</i> that stops the panel
/// working, and requiring the whole family name as a prefix is a strict enough test for that.
/// The cost is a false negative on a head someone renamed, which loses speed and nothing else.
/// </para>
/// </remarks>
public static class DraftModelLocator
{
    private const string Prefix = "mtp-";
    private const string Extension = ".gguf";

    /// <summary>
    /// The best head for <paramref name="modelFileName"/> among <paramref name="candidates"/>, or
    /// null when none of them names its family. Longest stem wins, so a head for a specific
    /// variant beats one for a whole family when both are present.
    /// </summary>
    public static string? Match(string modelFileName, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(modelFileName);
        ArgumentNullException.ThrowIfNull(candidates);

        // A head is not its own target. Selecting one as the model is a mistake a person can make
        // with a file picker, and pairing it with itself would turn that into a confusing load
        // failure rather than an ordinary one.
        if (IsHead(modelFileName))
        {
            return null;
        }

        string? best = null;
        var bestStem = 0;

        foreach (var candidate in candidates)
        {
            if (candidate is null || !IsHead(candidate))
            {
                continue;
            }

            var stem = Path.GetFileName(candidate)[Prefix.Length..^Extension.Length];
            if (stem.Length <= bestStem ||
                !modelFileName.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            best = candidate;
            bestStem = stem.Length;
        }

        return best;
    }

    /// <summary>
    /// Whether this filename is a drafting head rather than a model. Public because the models
    /// folder is listed to people: a head is not something anyone can ask a question of, so it
    /// belongs in neither the picker nor the largest-file fallback.
    /// </summary>
    public static bool IsDraftHead(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return IsHead(fileName);
    }

    private static bool IsHead(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            && name.Length > Prefix.Length + Extension.Length;
    }

    /// <summary>
    /// The head beside <paramref name="modelPath"/> in its own directory, or null when there is
    /// none. Enumeration failures are null: a missing head costs speed, and no reason to reach
    /// a directory is a reason to refuse to answer.
    /// </summary>
    public static string? FindBeside(string modelPath)
    {
        ArgumentNullException.ThrowIfNull(modelPath);

        var directory = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(directory, Prefix + "*" + Extension, SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return Match(Path.GetFileName(modelPath), candidates);
    }
}
