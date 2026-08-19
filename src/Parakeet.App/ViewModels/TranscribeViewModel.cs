using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parakeet.App.Services;
using Parakeet.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Formatting;
using Parakeet.Core.Jobs;
using Parakeet.Core.Segmentation;
using Parakeet.Core.Transcription;

namespace Parakeet.App.ViewModels;

public sealed partial class OutputFormatViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public OutputFormatViewModel(ITranscriptFormatter formatter, bool selected)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Id = formatter.Id;
        DisplayName = $"{formatter.DisplayName} ({formatter.FileExtension})";
        _isSelected = selected;
    }

    public string Id { get; }

    public string DisplayName { get; }
}

public sealed partial class TranscribeViewModel : ObservableObject
{
    /// <summary>
    /// How often the growing transcript is pushed to the window while a file decodes. Fast enough
    /// to read along with, slow enough that a three-hour file does not spend its time copying
    /// strings and re-laying-out a text block.
    /// </summary>
    private const int TranscriptRefreshMilliseconds = 250;

    private readonly IEngineProvider _engines;
    private readonly Func<EngineSelection> _selection;
    private readonly ModelSession? _session;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanRunAgain))]
    [NotifyPropertyChangedFor(nameof(StartHint))]
    [NotifyPropertyChangedFor(nameof(DropHint))]
    private bool _isRunning;

    [ObservableProperty]
    private string? _outputDirectory;

    [ObservableProperty]
    private JobViewModel? _selectedJob;

    [ObservableProperty]
    private string _liveTranscript = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _useFixedWindows;

    [ObservableProperty]
    private double _maxSegmentSeconds = 30;

    /// <summary>
    /// The speaker opt-in. Off by default and off every time the window opens: it reads each file
    /// a second time and runs a second model, and it is not what most transcriptions want.
    /// </summary>
    [ObservableProperty]
    private bool _labelSpeakers;

    public TranscribeViewModel(
        IEngineProvider engines, Func<EngineSelection> selection, ModelSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(engines);
        ArgumentNullException.ThrowIfNull(selection);

        _engines = engines;
        _selection = selection;
        _session = session;

        if (_session is not null)
        {
            _session.Changed += (_, _) =>
            {
                OnPropertyChanged(nameof(IsModelLoaded));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(StartHint));
            };
        }

        Formats = [.. TranscriptFormats.All
            .Where(f => f.Id != TranscriptFormats.Rttm.Id)
            .Select(f => new OutputFormatViewModel(f, f.Id is "txt" or "srt"))];
        RefreshSpeakerAvailability();
    }

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    /// <summary>
    /// The output formats on offer. Observable, and RTTM comes and goes with the diariser.
    /// </summary>
    /// <remarks>
    /// RTTM is the speaker opt-in's output and nothing else's, so it is offered only where the
    /// opt-in can be turned on: without a labeller it could only ever write an empty file, which is
    /// the same trap <c>transcribe</c> refuses on the command line. It has to be a live collection
    /// rather than a snapshot because the answer changes while the window is open — the diariser is
    /// a separate download, and a list fixed at construction would tell a user to install a model
    /// and then not notice that they had.
    /// </remarks>
    public ObservableCollection<OutputFormatViewModel> Formats { get; }

    /// <summary>
    /// What the drop zone says. It stops taking files while a batch runs, so while it is shut it
    /// says so rather than inviting a gesture <see cref="AddFiles"/> would refuse.
    /// </summary>
    public string DropHint => IsRunning
        ? "Adding files is off while a batch runs — press Cancel, or wait for it to finish."
        : "Drop audio or video files here";

    /// <summary>Extensions this build can actually open, for the file picker and the drop hint.</summary>
    public string SupportedExtensionsHint => string.Join("  ", AudioSources.SupportedExtensions);

    /// <summary>
    /// Whether the checkbox for the speaker opt-in does anything. When it does not, the box is
    /// disabled and <see cref="SpeakerHint"/> says why, rather than a setting that silently does
    /// nothing.
    /// </summary>
    public bool CanLabelSpeakers => _engines.SupportsSpeakerLabelling;

    /// <summary>
    /// Re-asks the engine provider whether a diariser is available, and brings the checkbox, the
    /// hint and the RTTM format into line with the answer.
    /// </summary>
    /// <remarks>
    /// Called at construction and whenever a model is installed or removed. Without it the hint
    /// reads "install it from the Models tab" for the rest of the session after the user has done
    /// exactly that — a dead end, and the one this window's own convention about disabled controls
    /// exists to avoid. It also turns the opt-in <em>off</em> when the model goes away, so a batch
    /// cannot start with the box ticked, no labeller behind it, and an empty .rttm as the result.
    /// </remarks>
    public void RefreshSpeakerAvailability()
    {
        var available = _engines.SupportsSpeakerLabelling;

        if (!available && LabelSpeakers)
        {
            LabelSpeakers = false;
        }

        var rttm = Formats.FirstOrDefault(f => f.Id == TranscriptFormats.Rttm.Id);
        if (available && rttm is null)
        {
            // Inserted where it belongs rather than appended: the list is rendered in order, and a
            // format that jumps to the end when a model is installed reads as a different list.
            var position = Math.Min(
                TranscriptFormats.All.ToList().FindIndex(f => f.Id == TranscriptFormats.Rttm.Id),
                Formats.Count);
            Formats.Insert(Math.Max(0, position), new OutputFormatViewModel(TranscriptFormats.Rttm, selected: false));
        }
        else if (!available && rttm is not null)
        {
            Formats.Remove(rttm);
        }

        OnPropertyChanged(nameof(CanLabelSpeakers));
        OnPropertyChanged(nameof(SpeakerHint));
    }

    public string? SpeakerHint => CanLabelSpeakers
        ? null
        : "Speaker labelling needs its own model, which is not installed yet. Install it from the Models tab; "
          + "it is a 453 MiB download and tells apart up to four speakers.";

    public bool HasJobs => Jobs.Count > 0;

    /// <summary>
    /// True when some row in the queue has not been transcribed yet — which is what Start runs.
    /// </summary>
    /// <remarks>
    /// A row that finished is done: its transcript, its output files and its "Done" are the result
    /// of a run that happened, and running it a second time costs minutes of decoding and writes
    /// <c>name (2).txt</c> beside the original. Failed and cancelled rows are not done and count
    /// here, because pressing Start after a failure is how a person retries one.
    /// </remarks>
    public bool HasWorkToDo => Jobs.Any(job => job.State != JobState.Completed);

    /// <summary>
    /// Whether there is a finished run to ask for a second time. See <see cref="RunAgainAsync"/>
    /// for why that is a button of its own rather than something Start decides.
    /// </summary>
    public bool CanRunAgain => !IsRunning && Jobs.Any(job => job.State == JobState.Completed);

    /// <summary>
    /// True when there is an engine to run with: a session holding a model, or the sessionless
    /// construction that builds its own engine per batch.
    /// </summary>
    public bool IsModelLoaded => _session?.IsLoaded ?? true;

    /// <summary>An enabled Start button with an empty queue does nothing when pressed, which reads
    /// as a broken button rather than an empty queue. The same is true of a Start with no model
    /// loaded, and of a Start with every file already transcribed, so all three are disabled here
    /// rather than failing at the press.</summary>
    public bool CanStart => !IsRunning && HasWorkToDo && IsModelLoaded;

    /// <summary>
    /// Says why Start is off when the reason is not simply an empty queue. A disabled button with
    /// no explanation is the same dead end as a button that does nothing — and "everything here is
    /// already transcribed" is the reason a person is least likely to guess, because the queue in
    /// front of them is full.
    /// </summary>
    public string? StartHint =>
        !IsModelLoaded ? "No model is loaded — open the Models tab and press Load."
        : HasJobs && !HasWorkToDo ? "Every file here is transcribed. 'Run again' runs them a second time; Clear empties the queue."
        : null;

    /// <summary>
    /// Re-asks the questions the buttons and their hints are computed from, all of which move
    /// together whenever the queue changes or a batch ends.
    /// </summary>
    private void RefreshQueueState()
    {
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(HasWorkToDo));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanRunAgain));
        OnPropertyChanged(nameof(StartHint));
    }

    /// <summary>
    /// Queues files, and refuses to while a batch is running.
    /// </summary>
    /// <remarks>
    /// Refused the way <see cref="Clear"/> is refused, and for a sharper reason than symmetry:
    /// <see cref="StartAsync"/> takes its work from a snapshot of the queue made before the first
    /// file is opened, so a row added after that is in neither the snapshot nor the results, and
    /// the reconciliation at the end of the batch has nothing to match it against. It sat blank at
    /// "Waiting" for ever beside "Finished 1 file." — the silent dead row that reconciliation
    /// exists to prevent, arriving by the one door it does not cover. The drop zone shuts at the
    /// same time (<see cref="DropHint"/>), so a drag is turned away by the cursor before it gets
    /// this far; this is the guard behind it, for the paths that do not go through the zone.
    /// </remarks>
    public void AddFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (IsRunning)
        {
            StatusMessage = "A batch is running — press Cancel, or wait for it to finish, before adding files.";
            return;
        }

        var added = 0;
        var rejected = new List<string>();

        foreach (var path in paths)
        {
            if (Jobs.Any(j => string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                rejected.Add($"{Path.GetFileName(path)}: not found");
                continue;
            }

            Jobs.Add(new JobViewModel(path));
            added++;
        }

        RefreshQueueState();

        StatusMessage = rejected.Count == 0
            ? added == 0 ? "Those files are already in the queue." : $"Added {added} file{(added == 1 ? string.Empty : "s")}."
            : string.Join("; ", rejected);
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsRunning)
        {
            return;
        }

        Jobs.Clear();
        LiveTranscript = string.Empty;
        StatusMessage = null;
        RefreshQueueState();
    }

    /// <summary>
    /// Puts every row back to waiting and runs the whole queue again.
    /// </summary>
    /// <remarks>
    /// Start runs what has not been run, so once every row says "Done" it is disabled, and there
    /// has to be a way to ask for the same files a second time — after changing the output formats,
    /// or turning the speaker opt-in on, both of which are reasons to want a transcript remade. A
    /// button of its own rather than something Start decides, because the two intentions are not
    /// distinguishable from a press and the cost of guessing wrong is minutes of decoding and a
    /// second copy of every output file.
    /// </remarks>
    [RelayCommand]
    private Task RunAgainAsync()
    {
        if (IsRunning || !HasJobs)
        {
            return Task.CompletedTask;
        }

        foreach (var job in Jobs)
        {
            job.Reset();
        }

        RefreshQueueState();
        return StartAsync();
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning || Jobs.Count == 0)
        {
            return;
        }

        if (!HasWorkToDo)
        {
            StatusMessage = "Every file here is transcribed already — press 'Run again' to run them a second time.";
            return;
        }

        var selection = _selection();
        if (_session is not null)
        {
            // With a session, the loaded engine is what runs — not whichever row happens to be
            // highlighted in the Models list, which may be a speaker model or nothing installed.
            // Only when nothing is loaded does the selection matter, and then "download one" is
            // the more useful instruction when there is nothing on disk to load in the first place.
            if (!_session.IsLoaded)
            {
                StatusMessage = _engines.IsModelAvailable(selection)
                    ? "No model is loaded. Open the Models tab, choose a backend and press Load."
                    : "No model is installed. Open the Models tab and download one first.";
                return;
            }
        }
        else if (!_engines.IsModelAvailable(selection))
        {
            StatusMessage = "No model is installed. Open the Models tab and download one first.";
            return;
        }

        var formats = Formats.Where(f => f.IsSelected).Select(f => f.Id).ToList();
        if (formats.Count == 0)
        {
            StatusMessage = "Choose at least one output format.";
            return;
        }

        if (!LabelSpeakers && formats.Contains(TranscriptFormats.Rttm.Id, StringComparer.Ordinal))
        {
            StatusMessage = "RTTM speaker turns need 'Label speakers' on: without it there are no turns to write.";
            return;
        }

        // The diariser is a file on disk and can go away between opening the window and pressing
        // Start — the Models tab will remove it, since only the *loaded* transcription engine is
        // protected there. Asked again here rather than trusted from construction, because the
        // alternative is a transcript with no names and a zero-byte .rttm reported as "Finished".
        if (LabelSpeakers && !_engines.SupportsSpeakerLabelling)
        {
            RefreshSpeakerAvailability();
            StatusMessage = "The speaker labelling model is no longer installed. Download it again from the Models tab.";
            return;
        }

        IsRunning = true;
        StatusMessage = null;
        LiveTranscript = string.Empty;

        _cancellation = new CancellationTokenSource();
        var ct = _cancellation.Token;

        // When the window has a session, the engine belongs to it and outlives this batch, so it is
        // borrowed here and never disposed here — loading is the Models tab's job, and the guard
        // above has already established one is resident. Without a session (the two-argument
        // construction) the engine is built and torn down inside this method as it always was.
        ITranscriptionEngine engine;
        ITranscriptionEngine? owned = null;

        try
        {
            if (_session is not null)
            {
                engine = _session.Engine
                    ?? throw new InvalidOperationException("No model is loaded.");
            }
            else
            {
                owned = _engines.Create(selection);
                engine = owned;
            }

            var options = BuildOptions();

            // One labeller for the whole batch, created only when the opt-in is on and the
            // provider has one to give; disposed with the batch.
            await using var labeller = LabelSpeakers && _engines.SupportsSpeakerLabelling
                ? _engines.CreateSpeakerLabeller()
                : null;

            if (LabelSpeakers && labeller is null)
            {
                StatusMessage = "The speaker labelling model is no longer installed. Download it again from the Models tab.";
                RefreshSpeakerAvailability();
                return;
            }

            // What has not been transcribed, which is not the same as what is in the queue. A row
            // that finished keeps its transcript, its outputs and its "Done"; the alternative is
            // that adding a fourth file to a queue of three re-decodes the three, which costs
            // minutes a file and leaves 'name (2).txt' beside every original. Failed and cancelled
            // rows are in here: pressing Start after a failure is how a person retries one.
            var pending = Jobs.Where(vm => vm.State != JobState.Completed).ToList();
            var alreadyDone = Jobs.Count - pending.Count;

            var jobs = pending.Select(vm => new TranscriptionJob
            {
                InputPath = vm.Path,
                Formats = formats,
                OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory) ? null : OutputDirectory,
            }).ToList();

            foreach (var vm in pending)
            {
                vm.Reset();
            }

            var runner = new BatchTranscriptionRunner((job, _, token) => RunJobAsync(engine, labeller, job, options, token));
            var results = await runner.RunAsync(jobs, progress: null, ct).ConfigureAwait(true);

            // The runner swallows per-file exceptions so the queue keeps going, which means a
            // failed or cancelled file never reaches the completion path inside RunJobAsync. If
            // its row is not updated here it sits at "Waiting" for ever with no error on it —
            // the silent failure this application exists to avoid, reproduced in its own UI.
            foreach (var result in results)
            {
                var vm = Jobs.FirstOrDefault(j =>
                    string.Equals(j.Path, result.Job.InputPath, StringComparison.OrdinalIgnoreCase));

                if (vm is { IsFinished: false })
                {
                    vm.Complete(result);
                }
            }

            var failed = results.Count(r => r.State == JobState.Failed);
            var cancelled = results.Count(r => r.State == JobState.Cancelled);

            var ran = failed == 0 && cancelled == 0
                ? $"Finished {results.Count} file{(results.Count == 1 ? string.Empty : "s")}."
                : $"Finished with {failed} failure{(failed == 1 ? string.Empty : "s")}" +
                  (cancelled > 0 ? $" and {cancelled} cancelled." : ".");

            // What was skipped is said out loud. A queue of four reporting that it finished one is
            // otherwise indistinguishable from a queue of four that lost three.
            StatusMessage = alreadyDone == 0
                ? ran
                : $"{ran} {alreadyDone} already transcribed and left alone.";
        }
#pragma warning disable CA1031 // Anything that escapes here belongs on screen, not in a crash dialog.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(true);
            }

            IsRunning = false;
            _cancellation?.Dispose();
            _cancellation = null;

            // The rows the batch just finished are what decide whether Start has anything left to
            // do and whether 'Run again' has anything to redo, so both are re-asked here.
            RefreshQueueState();
        }
    }

    private TranscriptionOptions BuildOptions()
    {
        var cap = TimeSpan.FromSeconds(Math.Clamp(MaxSegmentSeconds, 5, 60));
        var vad = (UseFixedWindows ? VoiceActivityOptions.Disabled : VoiceActivityOptions.Default) with
        {
            MaxSegmentLength = cap,
        };

        var options = new TranscriptionOptions
        {
            MaxSegmentLength = cap,
            VoiceActivity = vad,
        };

        options.Validate();
        return options;
    }

    private async Task<JobResult> RunJobAsync(
        ITranscriptionEngine engine,
        ISpeakerLabeller? labeller,
        TranscriptionJob job,
        TranscriptionOptions options,
        CancellationToken ct)
    {
        var vm = Jobs.First(j => string.Equals(j.Path, job.InputPath, StringComparison.OrdinalIgnoreCase));
        SelectedJob ??= vm;

        var started = DateTimeOffset.UtcNow;
        var text = new StringBuilder();

        await using var audio = AudioSources.Open(job.InputPath);

        var progress = new Progress<TranscriptionProgress>(vm.Apply);
        var segments = new List<TranscriptSegment>();

        // The transcript is streamed to the window as it is produced, because on a long recording
        // the alternative is a progress bar and nothing to read for a quarter of an hour. It is
        // published on a timer rather than per segment: rendering it every time costs a full copy
        // of the text plus a re-layout of the whole block, so on a three-hour file that is roughly
        // seventeen hundred copies of a string growing towards a third of a megabyte, and the
        // window stops responding long before the decode finishes.
        var lastPublished = Stopwatch.StartNew();

        void Publish()
        {
            vm.Transcript = text.ToString();
            if (ReferenceEquals(SelectedJob, vm))
            {
                LiveTranscript = vm.Transcript;
            }
        }

        await foreach (var segment in engine.TranscribeAsync(audio, options, progress, ct).ConfigureAwait(true))
        {
            segments.Add(segment);
            text.Append(segment.Text).Append(' ');

            if (lastPublished.ElapsedMilliseconds >= TranscriptRefreshMilliseconds)
            {
                Publish();
                lastPublished.Restart();
            }
        }

        Publish();

        var document = new TranscriptDocument
        {
            Segments = segments,
            SourceName = vm.FileName,
            AudioDuration = audio.Duration,
            ModelId = engine.Capabilities.ModelId,
            Backend = engine.Capabilities.Backend,
            ProcessingTime = DateTimeOffset.UtcNow - started,
        };

        string? speakerWarning = null;
        if (labeller is not null)
        {
            // The second pass. Both audio sources are single-read, so the file is opened again;
            // the labelled transcript then replaces the streamed one in the window, names in front.
            vm.Status = "Labelling speakers";
            await using var second = AudioSources.Open(job.InputPath);
            document = await SpeakerLabelling.LabelAsync(document, labeller, second, progress: progress, ct: ct).ConfigureAwait(true);
            speakerWarning = SpeakerLabelling.DescribeLimit(labeller, document);

            text.Clear();
            text.Append(JobViewModel.Render(document));
            Publish();
        }

        var written = await TranscriptWriter.WriteAsync(document, job, ct: ct).ConfigureAwait(true);

        var silence = DescribeSilence(engine, document);
        var result = new JobResult
        {
            Job = job,
            State = JobState.Completed,
            Document = document,
            OutputFiles = written,
            Elapsed = DateTimeOffset.UtcNow - started,
            Warning = silence is null ? speakerWarning : speakerWarning is null ? silence : $"{silence} {speakerWarning}",
        };

        vm.Complete(result);
        return result;
    }

    private static string? DescribeSilence(ITranscriptionEngine engine, TranscriptDocument document)
    {
        if (!document.IsEmpty)
        {
            return null;
        }

        var report = (engine as SegmentingTranscriptionEngine)?.LastSegmentationReport;

        if (report?.IsDigitalSilence == true)
        {
            return "This track is digitally silent — every sample is zero. If it should have sound, the wrong " +
                   "track or the wrong input was recorded.";
        }

        if (report?.LooksLikeMissedSpeech == true)
        {
            return "There is audio here but no speech was detected. Try 'fixed windows' below, which decodes " +
                   "everything instead of trusting the detector.";
        }

        return "No speech was found in this file.";
    }
}
