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

    /// <summary>
    /// This job with its outputs reduced to what a transcript written without the passes in
    /// <paramref name="failed"/> can honestly carry. A translation that did not happen means the
    /// plain stem rather than the <c>.en</c> one that promises English; speaker labels that did
    /// not happen mean no turns-only format, because an <c>.rttm</c> with no turns is the empty
    /// file the command line refuses to write when the opt-in is off.
    /// </summary>
    public TranscriptionJob WithoutFailedPasses(IReadOnlyList<PassFailure> failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        var job = this;

        if (failed.Any(failure => failure.Pass == OptInPass.Translation))
        {
            job = job with { StemSuffix = string.Empty };
        }

        if (failed.Any(failure => failure.Pass == OptInPass.Speakers))
        {
            job = job with
            {
                Formats = Formats
                    .Where(id => !TranscriptFormats.TryGet(id, out var format) || !ReferenceEquals(format, TranscriptFormats.Rttm))
                    .ToList(),
            };
        }

        return job;
    }
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

    /// <summary>
    /// The opt-in passes that failed for this file, when the transcript was written without them.
    /// Empty when every pass that was asked for ran. <see cref="State"/> stays
    /// <see cref="JobState.Completed"/> — the words are — and both surfaces read this beside it:
    /// the command line for its exit code and the window for the row's status.
    /// </summary>
    public IReadOnlyList<PassFailure> FailedPasses { get; init; } = [];
}

/// <summary>
/// One of the two opt-in passes over a finished transcript — speaker labels, the English version —
/// and the policy for when it fails: the transcript is handed back as it was, with the reason.
/// </summary>
/// <remarks>
/// <para>
/// The transcript is the product and the pass is a decoration of it, so a pass that fails for one
/// file costs that file its decoration and nothing else. Until 2026-08-22 it cost the file: the
/// labeller or the translator threw after the ASR pass had finished, the batch runner recorded the
/// job as failed, and minutes of decode went unwritten — for a source the sidecar could not read,
/// a segment refused for its length, or a sidecar that had died, in which case every remaining
/// file paid its own decode before failing the same way. The words were unaffected by any of it.
/// </para>
/// <para>
/// The failure is not swallowed. It comes back as a <see cref="PassFailure"/> whose sentence names
/// the pass and the reason; the job carries it in <see cref="JobResult.FailedPasses"/>; and both
/// surfaces say it where they say everything else — the command line on stderr and in its exit
/// code, the window on the row. Cancellation is not a failure and is not caught.
/// </para>
/// </remarks>
public sealed record OptInPass(string Name, string Product)
{
    /// <summary>The speaker pass: <see cref="Product"/> is "speaker labels".</summary>
    public static OptInPass Speakers { get; } = new("Speaker labelling", "speaker labels");

    /// <summary>The English pass: <see cref="Product"/> is "the English version".</summary>
    public static OptInPass Translation { get; } = new("Translation", "the English version");

    /// <summary>
    /// Runs <paramref name="run"/> over <paramref name="document"/> and returns what it produced,
    /// or — when it throws anything but a cancellation — <paramref name="document"/> unchanged and
    /// the failure.
    /// </summary>
    public async Task<(TranscriptDocument Document, PassFailure? Failure)> RunAsync(
        TranscriptDocument document,
        Func<Task<TranscriptDocument>> run)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(run);

        try
        {
            return (await run().ConfigureAwait(false), null);
        }
#pragma warning disable CA1031 // The pass is a decoration; the transcript under it is what the user waited for.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return (document, new PassFailure(this, ex.Message, ex));
        }
    }
}

/// <summary>An opt-in pass that failed for one file, and why.</summary>
public sealed record PassFailure(OptInPass Pass, string Reason, Exception? Exception = null)
{
    /// <summary>The sentence both surfaces print: which pass, that the transcript went out without its product, and the reason.</summary>
    public string Describe() =>
        $"{Pass.Name} failed for this file, so the transcript was written without {Pass.Product}: {Reason}";
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
            await WriteAtomicallyAsync(path, content, ct).ConfigureAwait(false);
            written.Add(path);
        }

        return written;
    }

    /// <summary>
    /// Writes beside the final name and moves into place, so a write that stops — cancelled, or
    /// the disk filling — leaves nothing under the final name.
    /// </summary>
    /// <remarks>
    /// Until 2026-08-22 the content went straight to the final name with a cancellable write, and a
    /// Ctrl-C mid-write left a truncated transcript that the Rename policy then treated as a finished
    /// one and wrote the next run beside.
    /// </remarks>
    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken ct)
    {
        var staging = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(staging, content, TextOutput.Utf8NoBom, ct).ConfigureAwait(false);
            File.Move(staging, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(staging);
            }
            catch (IOException)
            {
                // The staging file is the only thing left behind, and only when the disk or the
                // directory is already refusing; the original failure is the one to report.
            }
            catch (UnauthorizedAccessException)
            {
                // As above.
            }

            throw;
        }
    }

    /// <summary>
    /// Groups of jobs that would write to the same stem in the same directory — two inputs of one
    /// name in different folders under one output directory, or <c>a.wav</c> beside <c>a.mp3</c> —
    /// so a caller can refuse before the second quietly replaces, skips, or is renamed beside the
    /// first after the first was decoded.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TranscriptionJob>> FindOutputCollisions(IEnumerable<TranscriptionJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        // Windows paths compare without case; elsewhere they do not. The comparison is over the
        // destination, not the input, because that is where the collision happens.
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        return jobs
            .Where(job => job.Formats.Count > 0)
            .GroupBy(
                job => Path.Combine(
                    Path.GetFullPath(job.OutputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(job.InputPath)) ?? "."),
                    Path.GetFileNameWithoutExtension(job.InputPath) + job.StemSuffix),
                comparer)
            .Where(group => group.Count() > 1)
            .Select(group => (IReadOnlyList<TranscriptionJob>)group.ToList())
            .ToList();
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
