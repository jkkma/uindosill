using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parakeet.App.Services;
using Parakeet.Audio;
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

        Formats = [.. TranscriptFormats.All.Select(f => new OutputFormatViewModel(f, f.Id is "txt" or "srt"))];
    }

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public IReadOnlyList<OutputFormatViewModel> Formats { get; }

    /// <summary>Extensions this build can actually open, for the file picker and the drop hint.</summary>
    public string SupportedExtensionsHint => string.Join("  ", AudioSources.SupportedExtensions);

    public bool HasJobs => Jobs.Count > 0;

    /// <summary>
    /// True when there is an engine to run with: a session holding a model, or the sessionless
    /// construction that builds its own engine per batch.
    /// </summary>
    public bool IsModelLoaded => _session?.IsLoaded ?? true;

    /// <summary>An enabled Start button with an empty queue does nothing when pressed, which reads
    /// as a broken button rather than an empty queue. The same is true of a Start with no model
    /// loaded, so that is disabled here rather than failing at the press.</summary>
    public bool CanStart => !IsRunning && HasJobs && IsModelLoaded;

    /// <summary>
    /// Says why Start is off when the reason is a missing model. A disabled button with no
    /// explanation is the same dead end as a button that does nothing.
    /// </summary>
    public string? StartHint =>
        IsModelLoaded ? null : "No model is loaded — open the Models tab and press Load.";

    public void AddFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

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

        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(CanStart));

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
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(CanStart));
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

        var selection = _selection();
        if (!_engines.IsModelAvailable(selection))
        {
            StatusMessage = "No model is installed. Open the Models tab and download one first.";
            return;
        }

        // Checked after the installed test, because "download one" is the more useful instruction
        // when there is nothing on disk to load in the first place.
        if (_session is not null && !_session.IsLoaded)
        {
            StatusMessage = "No model is loaded. Open the Models tab, choose a backend and press Load.";
            return;
        }

        var formats = Formats.Where(f => f.IsSelected).Select(f => f.Id).ToList();
        if (formats.Count == 0)
        {
            StatusMessage = "Choose at least one output format.";
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

            var jobs = Jobs.Select(vm => new TranscriptionJob
            {
                InputPath = vm.Path,
                Formats = formats,
                OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory) ? null : OutputDirectory,
            }).ToList();

            foreach (var vm in Jobs)
            {
                vm.State = JobState.Pending;
                vm.Status = "Waiting";
                vm.Error = null;
                vm.Warning = null;
            }

            var runner = new BatchTranscriptionRunner((job, _, token) => RunJobAsync(engine, job, options, token));
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

            StatusMessage = failed == 0 && cancelled == 0
                ? $"Finished {results.Count} file{(results.Count == 1 ? string.Empty : "s")}."
                : $"Finished with {failed} failure{(failed == 1 ? string.Empty : "s")}" +
                  (cancelled > 0 ? $" and {cancelled} cancelled." : ".");
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
        ITranscriptionEngine engine, TranscriptionJob job, TranscriptionOptions options, CancellationToken ct)
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

        var written = await TranscriptWriter.WriteAsync(document, job, ct: ct).ConfigureAwait(true);

        var result = new JobResult
        {
            Job = job,
            State = JobState.Completed,
            Document = document,
            OutputFiles = written,
            Elapsed = DateTimeOffset.UtcNow - started,
            Warning = DescribeSilence(engine, document),
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
