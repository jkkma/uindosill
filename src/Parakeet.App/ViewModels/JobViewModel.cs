using CommunityToolkit.Mvvm.ComponentModel;
using Parakeet.Core.Jobs;
using Parakeet.Core.Transcription;

namespace Parakeet.App.ViewModels;

/// <summary>One file in the queue.</summary>
public sealed partial class JobViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyPropertyChangedFor(nameof(ProgressLabel))]
    private JobState _state = JobState.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressLabel))]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressLabel))]
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

    /// <summary>
    /// The transcript as the window draws it: one entry per segment, each carrying its speaker as
    /// a chip rather than as a prefix on the text.
    /// </summary>
    /// <remarks>
    /// Beside <see cref="Transcript"/> rather than instead of it. That string is what a person
    /// copies out of the window and what the pipeline's own tests pin; this is the view's shape.
    /// </remarks>
    public System.Collections.ObjectModel.ObservableCollection<TranscriptLineViewModel> Lines { get; } = [];

    public bool IsFinished => State is JobState.Completed or JobState.Failed or JobState.Cancelled;

    /// <summary>
    /// The percentage shown beside the file name while it is being transcribed, or null when there
    /// is no honest number to show.
    /// </summary>
    /// <remarks>
    /// Null rather than "0%" in three cases, and each is a different kind of nothing: a job that
    /// has not started, a job that has finished — where the row says how many files it wrote
    /// instead — and a stage that cannot report a fraction at all, which is what
    /// <see cref="IsIndeterminate"/> means. A determinate-looking 0% over an indeterminate bar
    /// claims a precision the pipeline has not got.
    ///
    /// Formatted invariantly because the surrounding interface is English throughout; rounding to
    /// whole percent keeps a decimal separator out of it either way.
    /// </remarks>
    public string? ProgressLabel =>
        State is JobState.Running && !IsIndeterminate
            ? Progress.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "%"
            : null;

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
            TranscriptionStage.Translating => "Translating",
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
        Lines.Clear();
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
            Relines(document);
        }
    }

    /// <summary>
    /// The transcript as the window shows it: <see cref="TranscriptDocument.Text"/> — the joined
    /// segments — with the speaker in front of each segment that has one. The document's own
    /// <c>Text</c> stays free of names on purpose: it is what the JSON's <c>text</c> field carries
    /// and what a word error rate is scored on, and a name is not a word anybody said.
    /// </summary>
    /// <summary>
    /// Rebuilds <see cref="Lines"/> from a finished document, assigning each speaker one of the
    /// four chip styles in the order they are first heard.
    /// </summary>
    /// <remarks>
    /// The diariser numbers speakers in the order it first hears them, and this follows that same
    /// order rather than sorting by name — so the chip a speaker gets does not move when a name is
    /// edited, and two files transcribed in the same session do not swap colours between them.
    ///
    /// The modulo is a backstop rather than a policy. Four is the diariser's architectural ceiling,
    /// so a fifth speaker is not something this pipeline can produce; if one ever arrives it wraps
    /// to the first chip rather than throwing, because a colour clash is a smaller failure than a
    /// window that will not draw a transcript.
    /// </remarks>
    private void Relines(TranscriptDocument document)
    {
        Lines.Clear();

        var chips = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var segment in document.Segments.Where(s => !s.IsEmpty))
        {
            var speaker = segment.Speaker;
            var chip = -1;

            if (speaker is not null)
            {
                if (!chips.TryGetValue(speaker, out chip))
                {
                    chip = chips.Count % 4;
                    chips[speaker] = chip;
                }
            }

            Lines.Add(new TranscriptLineViewModel(speaker, segment.Text.Trim(), chip));
        }
    }

    internal static string Render(TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return string.Join(" ", document.Segments
            .Where(s => !s.IsEmpty)
            .Select(s => s.Speaker is { } speaker ? $"{speaker}: {s.Text.Trim()}" : s.Text.Trim()));
    }
}
