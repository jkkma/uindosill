using Parakeet.Core.Formatting;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Jobs;

public enum JobState
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>What to do with an output file that already exists.</summary>
public enum OverwritePolicy
{
    /// <summary>Write <c>name (2).srt</c> rather than touching the existing file.</summary>
    Rename,

    Overwrite,

    Skip,
}

public sealed record TranscriptionJob
{
    public required string InputPath { get; init; }

    /// <summary>Formats to write. Empty means the caller only wants the document in memory.</summary>
    public IReadOnlyList<string> Formats { get; init; } = ["txt"];

    /// <summary>Directory for outputs. Null writes beside the input file.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>
    /// Inserted between the input's name and the format's extension, so a run that produces a
    /// different artefact from the same recording writes <c>call.en.srt</c> rather than
    /// <c>call.srt</c>. Empty by default: a plain transcription run's file names are what they
    /// always were.
    /// </summary>
    /// <remarks>
    /// This is what stops a translated run destroying a transcription run's output under
    /// <c>--overwrite</c>, and it is how SubRip carries the marker at all: SRT has no comment
    /// syntax, so the only place to say "this is the English one" is the name.
    /// </remarks>
    public string StemSuffix { get; init; } = string.Empty;

    public OverwritePolicy Overwrite { get; init; } = OverwritePolicy.Rename;

    public string DisplayName => Path.GetFileName(InputPath);
}

public sealed record JobResult
{
    public required TranscriptionJob Job { get; init; }

    public required JobState State { get; init; }

    public TranscriptDocument? Document { get; init; }

    /// <summary>Message shown to the user when <see cref="State"/> is <see cref="JobState.Failed"/>.</summary>
    public string? Error { get; init; }

    public Exception? Exception { get; init; }

    public TimeSpan Elapsed { get; init; }

    public IReadOnlyList<string> OutputFiles { get; init; } = [];

    /// <summary>Set when segmentation found nothing to decode; the caller must say so.</summary>
    public string? Warning { get; init; }
}

public sealed record BatchProgress
{
    public required int Completed { get; init; }

    public required int Total { get; init; }

    public required TranscriptionJob Current { get; init; }

    public TranscriptionProgress? JobProgress { get; init; }

    public double Fraction => Total == 0 ? 1d : Math.Clamp(Completed / (double)Total, 0d, 1d);
}

/// <summary>
/// Runs a list of files one after another and keeps going when one of them fails.
/// </summary>
/// <remarks>
/// Continue-on-error is the whole point: a queue of forty recordings that stops on the one
/// corrupt mp3 has thrown away the other thirty-nine, and the user finds out an hour later.
/// Failures are collected and reported per file.
/// </remarks>
public sealed class BatchTranscriptionRunner
{
    private readonly Func<TranscriptionJob, IProgress<TranscriptionProgress>?, CancellationToken, Task<JobResult>> _run;

    public BatchTranscriptionRunner(
        Func<TranscriptionJob, IProgress<TranscriptionProgress>?, CancellationToken, Task<JobResult>> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _run = run;
    }

    public async Task<IReadOnlyList<JobResult>> RunAsync(
        IReadOnlyList<TranscriptionJob> jobs,
        IProgress<BatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        var results = new List<JobResult>(jobs.Count);

        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];

            if (ct.IsCancellationRequested)
            {
                // Everything not yet started is reported as cancelled rather than dropped, so a
                // cancelled batch still accounts for every file the user handed it.
                results.Add(new JobResult { Job = job, State = JobState.Cancelled });
                continue;
            }

            var jobProgress = progress is null
                ? null
                : new Progress<TranscriptionProgress>(p => progress.Report(new BatchProgress
                {
                    Completed = results.Count,
                    Total = jobs.Count,
                    Current = job,
                    JobProgress = p,
                }));

            try
            {
                results.Add(await _run(job, jobProgress, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                results.Add(new JobResult { Job = job, State = JobState.Cancelled });
            }
#pragma warning disable CA1031 // One bad file must not take the rest of the queue with it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                results.Add(new JobResult
                {
                    Job = job,
                    State = JobState.Failed,
                    Error = ex.Message,
                    Exception = ex,
                });
            }

            progress?.Report(new BatchProgress
            {
                Completed = results.Count,
                Total = jobs.Count,
                Current = job,
            });
        }

        return results;
    }
}

/// <summary>Writes a finished transcript to disk in the requested formats.</summary>
public static class TranscriptWriter
{
    public static async Task<IReadOnlyList<string>> WriteAsync(
        TranscriptDocument document,
        TranscriptionJob job,
        TranscriptFormatOptions? formatOptions = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(job);

        if (job.Formats.Count == 0)
        {
            return [];
        }

        var directory = job.OutputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(job.InputPath)) ?? ".";
        Directory.CreateDirectory(directory);

        var stem = Path.GetFileNameWithoutExtension(job.InputPath) + job.StemSuffix;
        var written = new List<string>(job.Formats.Count);

        foreach (var formatId in job.Formats)
        {
            ct.ThrowIfCancellationRequested();

            var formatter = TranscriptFormats.Get(formatId);
            var path = ResolvePath(directory, stem, formatter.FileExtension, job.Overwrite);
            if (path is null)
            {
                continue;
            }

            var content = formatter.Format(document, formatOptions);
            await File.WriteAllTextAsync(path, content, TextOutput.Utf8NoBom, ct).ConfigureAwait(false);
            written.Add(path);
        }

        return written;
    }

    internal static string? ResolvePath(string directory, string stem, string extension, OverwritePolicy policy)
    {
        var path = Path.Combine(directory, stem + extension);
        if (!File.Exists(path))
        {
            return path;
        }

        switch (policy)
        {
            case OverwritePolicy.Overwrite:
                return path;

            case OverwritePolicy.Skip:
                return null;

            case OverwritePolicy.Rename:
            default:
                for (var i = 2; i < 1000; i++)
                {
                    var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
                    if (!File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                throw new IOException($"Could not find a free output name for '{stem}{extension}' in '{directory}'.");
        }
    }
}
