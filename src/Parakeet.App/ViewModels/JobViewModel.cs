using CommunityToolkit.Mvvm.ComponentModel;
using Parakeet.Core.Jobs;
using Parakeet.Core.Transcription;

namespace Parakeet.App.ViewModels;

/// <summary>One file in the queue.</summary>
public sealed partial class JobViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    private JobState _state = JobState.Pending;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private string _status = "Waiting";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private string _transcript = string.Empty;

    public JobViewModel(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }

    public string FileName { get; }

    public List<string> OutputFiles { get; } = [];

    public bool IsFinished => State is JobState.Completed or JobState.Failed or JobState.Cancelled;

    public void Apply(TranscriptionProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        // Progress<T> posts through the synchronisation context, so a report raised just before
        // the job finished can arrive just after it. Without this guard that late report puts a
        // completed job back into "Running" and it stays there — the file is transcribed, the
        // output is written, and the row says "Transcribing…" for ever.
        if (IsFinished)
        {
            return;
        }

        State = JobState.Running;
        IsIndeterminate = progress.Fraction is null;
        Progress = (progress.Fraction ?? 0) * 100;
        Status = progress.Stage switch
        {
            TranscriptionStage.Reading => "Reading",
            TranscriptionStage.Segmenting => "Segmenting",
            TranscriptionStage.Decoding => $"Transcribing {progress.Processed:hh\\:mm\\:ss}",
            TranscriptionStage.Finalising => "Finishing",
            TranscriptionStage.LabellingSpeakers => "Labelling speakers",
            _ => "Working",
        };
    }

    /// <summary>
    /// Puts the row back to waiting, so the file can be run again.
    /// </summary>
    /// <remarks>
    /// Everything a run left on the row goes, not only the state: a row that says "Waiting" while
    /// still showing the last run's transcript, its progress bar full and its old output files
    /// listed is describing a run that is no longer happening.
    /// </remarks>
    public void Reset()
    {
        State = JobState.Pending;
        Status = "Waiting";
        Progress = 0;
        IsIndeterminate = false;
        Error = null;
        Warning = null;
        Transcript = string.Empty;
        OutputFiles.Clear();
    }

    public void Complete(JobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        State = result.State;
        IsIndeterminate = false;
        Progress = 100;
        Error = result.Error;
        Warning = result.Warning;
        OutputFiles.Clear();
        OutputFiles.AddRange(result.OutputFiles);

        Status = result.State switch
        {
            JobState.Completed when result.OutputFiles.Count > 0 =>
                $"Done — {result.OutputFiles.Count} file{(result.OutputFiles.Count == 1 ? string.Empty : "s")}",
            JobState.Completed => "Done",
            JobState.Failed => "Failed",
            JobState.Cancelled => "Cancelled",
            _ => "Done",
        };

        if (result.Document is { } document)
        {
            Transcript = Render(document);
        }
    }

    /// <summary>
    /// The transcript as the window shows it: <see cref="TranscriptDocument.Text"/> — the joined
    /// segments — with the speaker in front of each segment that has one. The document's own
    /// <c>Text</c> stays free of names on purpose: it is what the JSON's <c>text</c> field carries
    /// and what a word error rate is scored on, and a name is not a word anybody said.
    /// </summary>
    internal static string Render(TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return string.Join(" ", document.Segments
            .Where(s => !s.IsEmpty)
            .Select(s => s.Speaker is { } speaker ? $"{speaker}: {s.Text.Trim()}" : s.Text.Trim()));
    }
}
