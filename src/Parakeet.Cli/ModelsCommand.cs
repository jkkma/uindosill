using System.Globalization;
using Parakeet.Core.Licensing;
using Parakeet.Core.Models;

namespace Parakeet.Cli;

internal static class ModelsCommand
{
    public static async Task<int> RunAsync(CliContext context, ParsedCommandLine parsed, CancellationToken ct)
    {
        var subcommand = parsed.Positionals.Count > 0 ? parsed.Positionals[0].ToLowerInvariant() : "list";

        return subcommand switch
        {
            "list" => List(context),
            "path" => Path(context),
            "download" or "install" => await DownloadAsync(context, parsed, ct).ConfigureAwait(false),
            "remove" or "uninstall" => Remove(context, parsed),
            "verify" => await VerifyAsync(context, parsed, ct).ConfigureAwait(false),
            _ => Unknown(context, subcommand),
        };
    }

    private static int Unknown(CliContext context, string subcommand)
    {
        context.WriteError($"Unknown models subcommand '{subcommand}'. Use list, download, remove, verify or path.");
        return ExitCodes.UsageError;
    }

    private static int Path(CliContext context)
    {
        context.WriteLine(context.Store.RootDirectory);
        return ExitCodes.Success;
    }

    private static int List(CliContext context)
    {
        context.WriteLine($"Model directory: {context.Store.RootDirectory}");
        context.WriteLine();

        foreach (var model in context.Catalog.Models)
        {
            var installed = context.Store.IsInstalled(model);
            var marks = new List<string>();
            if (installed)
            {
                marks.Add("installed");
            }

            if (model.Recommended)
            {
                marks.Add("recommended");
            }

            // Only said when it is not the obvious thing: every entry transcribes unless it says otherwise.
            if (model.Task != ModelTask.Transcription)
            {
                marks.Add($"{model.Task.ToString().ToLowerInvariant()} model — not selectable for transcribe");
            }

            if (!model.Verified)
            {
                marks.Add("unverified catalogue entry");
            }

            if (!model.IsFullyPinned)
            {
                var unpinned = model.Files.Count(f => f.Sha256 is null);
                marks.Add(model.Files.Count == 1
                    ? "no pinned digest"
                    : $"no pinned digest for {unpinned} of {model.Files.Count} files");
            }

            context.WriteLine($"{model.Id}");
            context.WriteLine($"  {model.DisplayName}  [{string.Join(", ", marks)}]");
            context.WriteLine($"  licence: {model.License}");

            // Said only for an entry that is more than one file, because for the other six saying
            // "1 file" would be noise on every line of a list a user reads often.
            if (model.IsMultiFile)
            {
                context.WriteLine(
                    $"  {model.Files.Count} files in {model.StorageName}\\  " +
                    $"({(model.TotalSizeBytes is { } bytes ? Bytes(bytes) + " total" : "total size not pinned")})");
            }

            if (model.Languages.Count > 0)
            {
                context.WriteLine($"  languages: {string.Join(" ", model.Languages)}");
            }

            if (model.Notes is { Length: > 0 } notes)
            {
                context.WriteLine($"  note: {notes}");
            }

            context.WriteLine();
        }

        var sideloaded = context.Store is LocalModelStore local
            ? local.ListInstalled(context.Catalog).Where(m => m.IsSideloaded).ToList()
            : [];

        if (sideloaded.Count > 0)
        {
            context.WriteLine("Sideloaded files in the model directory:");
            foreach (var model in sideloaded)
            {
                context.WriteLine($"  {System.IO.Path.GetFileName(model.Path)}  ({Bytes(model.SizeBytes)})");
            }
        }

        return ExitCodes.Success;
    }

    private static async Task<int> DownloadAsync(CliContext context, ParsedCommandLine parsed, CancellationToken ct)
    {
        if (parsed.Positionals.Count < 2)
        {
            context.WriteError("models download needs a model id. Run 'uindosill models list'.");
            return ExitCodes.UsageError;
        }

        if (!context.Catalog.TryGet(parsed.Positionals[1], out var model))
        {
            context.WriteError($"Unknown model '{parsed.Positionals[1]}'.");
            return ExitCodes.UsageError;
        }

        var attribution = Attributions.Get(model.AttributionId);
        context.WriteLine(attribution.ToPlainText());

        using var installer = new ModelInstaller(context.Store);
        var options = new ModelInstallOptions
        {
            AllowUnverified = parsed.HasFlag("allow-unverified"),
            Force = parsed.HasFlag("force"),
        };

        var progress = context.Interactive
            ? new Progress<ModelInstallProgress>(p => WriteProgress(context, p))
            : null;

        try
        {
            var result = await installer.InstallAsync(model, options, progress, ct).ConfigureAwait(false);

            if (context.Interactive)
            {
                context.Error.Write('\r');
                context.Error.Write(new string(' ', 78));
                context.Error.Write('\r');
            }

            context.WriteLine(result.AlreadyPresent
                ? $"{model.Id} is already installed at {result.Model.Path}"
                : $"Installed {model.Id} to {result.Model.Path}");

            context.WriteLine($"  size:   {Bytes(result.Model.SizeBytes)}");

            // One line per file, always — including for the five single-file entries, where it
            // reads exactly as the old one line did. An entry of nine files that printed one digest
            // would be offering a release engineer an eighth of what they have to paste back.
            foreach (var file in result.Files)
            {
                context.WriteLine(result.Files.Count == 1
                    ? $"  sha256: {file.Sha256}"
                    : $"  sha256: {file.Sha256}  {file.FileName}");
            }

            if (!model.IsFullyPinned)
            {
                context.WriteLine();
                context.WriteLine(
                    "This entry had no pinned digest. Copy the sha256 above into models.json and set \"verified\": " +
                    "true so the next install is checked rather than trusted.");
            }

            return ExitCodes.Success;
        }
        catch (ModelInstallException ex)
        {
            context.WriteError(ex.Message);
            return ExitCodes.RuntimeError;
        }
    }

    private static async Task<int> VerifyAsync(CliContext context, ParsedCommandLine parsed, CancellationToken ct)
    {
        if (parsed.Positionals.Count < 2)
        {
            context.WriteError("models verify needs a model id.");
            return ExitCodes.UsageError;
        }

        if (!context.Catalog.TryGet(parsed.Positionals[1], out var model))
        {
            context.WriteError($"Unknown model '{parsed.Positionals[1]}'.");
            return ExitCodes.UsageError;
        }

        if (!context.Store.IsInstalled(model))
        {
            context.WriteError($"{model.Id} is not installed ({context.Store.PathFor(model)}).");
            return ExitCodes.RuntimeError;
        }

        context.WriteLine($"path:   {context.Store.PathFor(model)}");

        // Every file is hashed and reported even after the first mismatch. Stopping at the first
        // would answer "is it good" and not "what is wrong with it", and for a nine-file entry the
        // second question is the one somebody is actually asking.
        var mismatches = 0;
        var unpinned = 0;

        foreach (var file in model.Files)
        {
            var path = context.Store.PathFor(model, file);
            var digest = await ModelInstaller.ComputeSha256Async(path, ct).ConfigureAwait(false);
            var label = model.Files.Count == 1 ? string.Empty : $"{file.FileName}: ";

            context.WriteLine($"{label}size:   {Bytes(new FileInfo(path).Length)}");
            context.WriteLine($"{label}sha256: {digest}");

            if (file.Sha256 is null)
            {
                context.WriteLine($"{label}catalogue: no pinned digest to compare against.");
                unpinned++;
                continue;
            }

            var matched = string.Equals(digest, file.Sha256, StringComparison.OrdinalIgnoreCase);
            context.WriteLine($"{label}catalogue: {file.Sha256}");
            context.WriteLine($"{label}{(matched ? "match" : "MISMATCH")}");
            if (!matched)
            {
                mismatches++;
            }
        }

        if (model.Files.Count > 1)
        {
            context.WriteLine(
                $"{model.Files.Count - mismatches - unpinned} of {model.Files.Count} files match" +
                (unpinned > 0 ? $", {unpinned} unpinned" : string.Empty) +
                (mismatches > 0 ? $", {mismatches} MISMATCH" : string.Empty));
        }

        return mismatches == 0 ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    private static int Remove(CliContext context, ParsedCommandLine parsed)
    {
        if (parsed.Positionals.Count < 2)
        {
            context.WriteError("models remove needs a model id.");
            return ExitCodes.UsageError;
        }

        if (!context.Catalog.TryGet(parsed.Positionals[1], out var model))
        {
            context.WriteError($"Unknown model '{parsed.Positionals[1]}'.");
            return ExitCodes.UsageError;
        }

        var removed = context.Store.Remove(model);
        context.WriteLine(removed
            ? $"Removed {model.Id}."
            : $"{model.Id} was not installed.");

        return ExitCodes.Success;
    }

    private static void WriteProgress(CliContext context, ModelInstallProgress progress)
    {
        var percent = progress.Fraction is { } f ? $"{f * 100:0.0}%" : "  ?";
        var speed = progress.BytesPerSecond is { } bps ? $"{Bytes((long)bps)}/s" : string.Empty;

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"\r{progress.Phase,-12} {percent,6} {Bytes(progress.BytesCompleted),10} {speed,12}");

        context.Error.Write(line.PadRight(78));
    }

    internal static string Bytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double size = value;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{size:0.##} {units[unit]}");
    }
}
