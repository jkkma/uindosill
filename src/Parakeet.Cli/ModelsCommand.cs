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

            if (model.Sha256 is null)
            {
                marks.Add("no pinned digest");
            }

            context.WriteLine($"{model.Id}");
            context.WriteLine($"  {model.DisplayName}  [{string.Join(", ", marks)}]");
            context.WriteLine($"  licence: {model.License}");

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
            context.WriteLine($"  sha256: {result.Sha256}");

            if (model.Sha256 is null)
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

        var path = context.Store.PathFor(model);
        if (!File.Exists(path))
        {
            context.WriteError($"{model.Id} is not installed ({path}).");
            return ExitCodes.RuntimeError;
        }

        var digest = await ModelInstaller.ComputeSha256Async(path, ct).ConfigureAwait(false);
        context.WriteLine($"path:   {path}");
        context.WriteLine($"size:   {Bytes(new FileInfo(path).Length)}");
        context.WriteLine($"sha256: {digest}");

        if (model.Sha256 is null)
        {
            context.WriteLine("catalogue: no pinned digest to compare against.");
            return ExitCodes.Success;
        }

        var matches = string.Equals(digest, model.Sha256, StringComparison.OrdinalIgnoreCase);
        context.WriteLine($"catalogue: {model.Sha256}");
        context.WriteLine(matches ? "match" : "MISMATCH");
        return matches ? ExitCodes.Success : ExitCodes.RuntimeError;
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
