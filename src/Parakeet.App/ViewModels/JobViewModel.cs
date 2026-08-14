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

        State = JobState.Running;
        IsIndeterminate = progress.Fraction is null;
        Progress = (progress.Fraction ?? 0) * 100;
        Status = progress.Stage switch
        {
            TranscriptionStage.Reading => "Reading",
            TranscriptionStage.Segmenting => "Segmenting",
            TranscriptionStage.Decoding => $"Transcribing {progress.Processed:hh\\:mm\\:ss}",
            TranscriptionStage.Finalising => "Finishing",
            _ => "Working",
        };
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
            Transcript = document.Text;
        }
    }
}
