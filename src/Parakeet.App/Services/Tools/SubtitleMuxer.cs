using System.Diagnostics;
using Parakeet.Core.Muxing;

namespace Parakeet.App.Services.Tools;

/// <summary>Thrown when a transcript could not be put inside its recording.</summary>
public sealed class SubtitleMuxException : Exception
{
    public SubtitleMuxException(string message) : base(message)
    {
    }

    public SubtitleMuxException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>Puts a transcript inside the recording it came from.</summary>
public interface ISubtitleMuxer
{
    /// <summary>Whether this build can do it at all.</summary>
    bool IsAvailable { get; }

    /// <summary>Why it cannot, or null when it can.</summary>
    string? DescribeUnavailable();

    /// <summary>
    /// Writes a new file carrying <paramref name="subtitlePath"/> as a track, and returns its path.
    /// </summary>
    Task<string> MuxAsync(
        SubtitleMuxPlan plan,
        string subtitlePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The real one: the vendored ffmpeg, run as a child process, copying every stream.
/// </summary>
/// <remarks>
/// <para>
/// The work and the reasoning are both in <see cref="SubtitleMux"/> — which container a format
/// needs and what the command line is. What is here is spawning it, reporting what went wrong in
/// words a reader can act on, and never writing over the file it was given.
/// </para>
/// <para>
/// <b>The output is built beside the target and moved into place.</b> A remux rewrites the whole
/// file: for a three-hour recording that is gigabytes of copying, and a cancel or a power cut
/// halfway through a direct write leaves a truncated file wearing a finished name. Writing to a
/// temporary name in the same directory — the same directory, so the move is a rename rather than a
/// second copy — means an interrupted run leaves rubbish nobody will mistake for a result.
/// </para>
/// <para>
/// Nothing in the suite runs this class: it needs the vendored binary and real media. The planner
/// it drives is tested exhaustively, and its argument list was driven against FFmpeg 9.0.1 by hand
/// on 2026-08-23 over five input-and-format pairs. See <c>docs/UNPROVEN.md</c>.
/// </para>
/// </remarks>
public sealed class FfmpegSubtitleMuxer : ISubtitleMuxer
{
    public bool IsAvailable => BundledTools.FfmpegPath is not null;

    public string? DescribeUnavailable() =>
        IsAvailable
            ? null
            : "This build cannot add a transcript to a media file: ffmpeg was not vendored. "
              + "Run scripts/vendor-tools.ps1 — see docs/NATIVE-BINARIES.md. "
              + "The transcript files beside the recording are unaffected.";

    public async Task<string> MuxAsync(
        SubtitleMuxPlan plan,
        string subtitlePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(subtitlePath);

        if (BundledTools.FfmpegPath is not { } ffmpeg)
        {
            throw new SubtitleMuxException(DescribeUnavailable()!);
        }

        if (!File.Exists(plan.InputPath))
        {
            throw new SubtitleMuxException($"The recording is no longer at {plan.InputPath}.");
        }

        if (!File.Exists(subtitlePath))
        {
            throw new SubtitleMuxException($"The transcript is no longer at {subtitlePath}.");
        }

        var output = Unique(plan.OutputPath);
        var staging = Path.Combine(
            Path.GetDirectoryName(output) ?? ".",
            "." + Path.GetFileNameWithoutExtension(output) + "-" + Guid.NewGuid().ToString("N")[..8]
                + Path.GetExtension(output));

        progress?.Report("Adding the transcript to the recording");

        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in SubtitleMux.Arguments(plan, subtitlePath, staging))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        var errors = new List<string>();

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                errors.Add(e.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            Discard(staging);
            throw;
        }
        catch (Exception ex) when (ex is not SubtitleMuxException)
        {
            Discard(staging);
            throw new SubtitleMuxException($"ffmpeg could not be run: {ex.Message}", ex);
        }

        if (process.ExitCode != 0)
        {
            Discard(staging);

            // ffmpeg's last line is the one that says what it refused, and the whole of stderr is
            // a wall nobody will read. Where there is nothing at all, the exit code is all there is.
            var reason = errors.Count > 0 ? errors[^1] : $"ffmpeg exited with {process.ExitCode}.";
            throw new SubtitleMuxException(reason);
        }

        File.Move(staging, output);
        return output;
    }

    /// <summary>
    /// <paramref name="path"/>, or the next free <c>name (2)</c> beside it.
    /// </summary>
    /// <remarks>
    /// The same answer <see cref="Parakeet.Core.Jobs.OverwritePolicy.Rename"/> gives for a
    /// transcript file, for the same reason: a second run is somebody asking for another one, not
    /// asking for the first to be destroyed.
    /// </remarks>
    private static string Unique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new SubtitleMuxException($"There are already a thousand files called {stem}.");
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone between the check and the kill, which is the normal race.
        }
    }

    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A half-written file we could not remove is worth leaving rather than failing the
            // error path over — it carries the dot prefix and the guid that say what it is.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
