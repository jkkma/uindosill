using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parakeet.App.Services;
using Parakeet.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Formatting;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;
using Parakeet.Core.Segmentation;
using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;

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

    /// <summary>
    /// What goes between a translated run's file name and its extension, so its output is its own
    /// rather than the transcription run's.
    /// </summary>
    private const string TranslatedInfix = "." + TranslationTarget.LanguageTag;

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
    [NotifyPropertyChangedFor(nameof(VisibleLines))]
    [NotifyPropertyChangedFor(nameof(CanShowTranslation))]
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
    [NotifyPropertyChangedFor(nameof(CanSetSpeakerCount))]
    [NotifyPropertyChangedFor(nameof(SpeakerCountHint))]
    [NotifyPropertyChangedFor(nameof(SpeakerDurationWarning))]
    private bool _labelSpeakers;

    /// <summary>
    /// How many people are talking, when the user knows. Null — the field left blank — lets the
    /// labeller estimate it, which is what it has always done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the window's half of the one repair this product has for a long recording. The
    /// shipping diariser cannot be told a count, so the number does not steer the model: the labels
    /// it produces are folded down to this many afterwards, merging the pair that talk over each
    /// other least. That works because the failure past the length its labels are established to is
    /// always over-segmentation — one person's identity drifting onto a second label — and merging
    /// is the direction that can be repaired, where splitting one label back into two people is not.
    /// </para>
    /// <para>
    /// Blank rather than a default of two, and blank is not "no opinion" being tidy. The fold is
    /// wrong to apply unasked: on one of the eighteen AMI development meetings the pair of
    /// <em>genuinely different</em> speakers who collide least never overlap at all across the whole
    /// meeting, so an automatic rule would merge two real people there. It fires only when somebody
    /// says they know the number.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeakerCountHint))]
    private int? _speakerCount;

    /// <summary>
    /// The English opt-in. Off by default and off every time the window opens, on the same terms
    /// as the speaker one: it runs a second model over every segment, and most transcriptions are
    /// already in the language somebody wanted.
    /// </summary>
    [ObservableProperty]
    private bool _translateToEnglish;

    /// <summary>
    /// Which pane the transcript area is showing: 0 the transcript, 1 the English.
    /// </summary>
    /// <remarks>
    /// On the view model rather than on the row, so switching files keeps the pane a person chose
    /// rather than snapping back to the source every time they click down the queue. A row with no
    /// translation hides the switcher entirely and <see cref="VisibleLines"/> falls back to the
    /// transcript, so the index cannot strand anybody on an empty pane.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleLines))]
    private int _transcriptPane;

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
        RefreshTranslationAvailability();
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
        OnPropertyChanged(nameof(CanSetSpeakerCount));
        OnPropertyChanged(nameof(SpeakerCountHint));
        OnPropertyChanged(nameof(SpeakerDurationWarning));
    }

    /// <summary>
    /// Why the opt-in is off, when it is. Asked of the provider rather than stated here: since the
    /// diariser moved into the bundled Python there are two reasons it can be unavailable and only
    /// the provider can tell which one applies.
    /// </summary>
    public string? SpeakerHint => CanLabelSpeakers ? null : _engines.DescribeUnavailable(ModelTask.Diarisation);

    /// <summary>
    /// Whether the speaker-count field is live. Only with the opt-in on: a count with nothing to
    /// count is the silently-inert setting this window refuses to draw.
    /// </summary>
    public bool CanSetSpeakerCount => CanLabelSpeakers && LabelSpeakers;

    /// <summary>
    /// What the count will actually do, said before it is acted on — including when it cannot be
    /// done at all.
    /// </summary>
    /// <remarks>
    /// The window's version of what <c>LabellerFactory</c> prints on the command line, and it says
    /// the same two things for the same reasons. A count the labeller cannot be told is honoured by
    /// folding afterwards, and a user is owed that distinction rather than left to assume the model
    /// was steered. A count <em>above the cap</em> is a different fact and the one that changes what
    /// somebody does next: it was never reachable, the fold has nothing to fold, and afterwards the
    /// only sentence left is "4 speakers were labelled", which reads as a fact about the recording
    /// rather than about the tool.
    /// </remarks>
    public string? SpeakerCountHint
    {
        get
        {
            if (!CanSetSpeakerCount || _engines.SpeakerLimits is not { } limits)
            {
                return null;
            }

            if (SpeakerCount is not { } count)
            {
                // Blank stops being an option once something in the queue is past the bound, and the
                // hint has to say so here rather than let Start be the first place anybody hears it.
                return LongestPastTheBound() is { } risky
                    ? $"{risky.FileName} is past that bound, so this one needs a count: the model would estimate "
                      + "it, and past about an hour it tends to hear one person as two. Give the number, or turn "
                      + "'Label speakers' off and take the transcript without names."
                    : "Leave this blank and the model estimates it. Set it when you know: past about an hour "
                      + "this model tends to hear one person as two, and a count is the only repair for that.";
            }

            if (SpeakerLabelling.DescribeUnreachableCount(limits, count) is { } unreachable)
            {
                return unreachable;
            }

            return limits.SupportsFixedSpeakerCount
                ? null
                : $"{limits.Name} estimates the count itself and cannot be told one, so its "
                  + $"labels are folded down to {count} afterwards, merging the pair that talk over each other least. "
                  + "If it finds that many or fewer, nothing is merged.";
        }
    }

    /// <summary>
    /// The long-recording warning, in front of the person who can still act on it. Null when the
    /// opt-in is off, when the labeller has no such bound, or when everything queued is inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The half that was missing. The command line fires this before the labeller decodes a sample;
    /// the window had only <see cref="SpeakerLabelling.DescribeLimit"/>, which is an
    /// <em>afterwards</em> sentence and a differently-shaped one — it reports what happened, where
    /// this reports what is not known to work. So a two-hour recording labelled through this window
    /// came back with speaker names and nothing saying they were past where the evidence stops.
    /// </para>
    /// <para>
    /// Drawn from the durations read when the files were queued, so it is on screen while the count
    /// beside it can still be set and before any of the twenty minutes are spent. The longest file
    /// carries the sentence, because it is the worst case and the one that names a real number.
    /// </para>
    /// </remarks>
    public string? SpeakerDurationWarning
    {
        get
        {
            if (!CanSetSpeakerCount || _engines.SpeakerLimits is not { } limits)
            {
                return null;
            }

            var over = JobsPastTheBound().ToList();
            if (over.Count == 0)
            {
                return null;
            }

            var longest = over.MaxBy(job => job.Duration!.Value)!;
            var sentence = SpeakerLabelling.DescribeDurationRisk(limits, longest.Duration);

            return over.Count == 1
                ? $"{longest.FileName}: {sentence}"
                : $"{over.Count} of the files queued are longer than that. The longest, {longest.FileName}: {sentence}";
        }
    }

    /// <summary>Every queued file longer than the labeller's labels have been established on.</summary>
    private IEnumerable<JobViewModel> JobsPastTheBound() =>
        _engines.SpeakerLimits is { } limits
            ? Jobs.Where(job => SpeakerLabelling.DescribeDurationRisk(limits, job.Duration) is not null)
            : [];

    /// <summary>
    /// The longest queued file past that bound, or null when there is none.
    /// </summary>
    /// <remarks>
    /// What both the hint and the guard at Start are computed from, so the sentence a person reads
    /// beside the field and the one that stops the batch cannot disagree about which file is the
    /// problem.
    /// </remarks>
    private JobViewModel? LongestPastTheBound() => JobsPastTheBound().MaxBy(job => job.Duration!.Value);

    /// <summary>
    /// How long a recording this labeller's labels have been established on, as a phrase. Invariant
    /// because the surrounding interface is English throughout, and whole minutes keep a decimal
    /// separator out of it either way.
    /// </summary>
    private string EstablishedLength => _engines.SpeakerLimits?.ReliableUpTo is { } bound
        ? string.Create(CultureInfo.InvariantCulture, $"{bound.TotalMinutes:F0} minutes")
        : "this model's established length";

    /// <summary>
    /// Whether the English opt-in does anything. Disabled with a reason when it does not, on the
    /// same terms as the speaker one.
    /// </summary>
    public bool CanTranslate => _engines.SupportsTranslation;

    /// <summary>The twin of <see cref="SpeakerHint"/>, and for the same two reasons.</summary>
    public string? TranslationHint => CanTranslate ? null : _engines.DescribeUnavailable(ModelTask.Translation);

    /// <summary>
    /// Re-asks whether a translator is available and brings the checkbox and its hint into line.
    /// </summary>
    /// <remarks>
    /// The twin of <see cref="RefreshSpeakerAvailability"/> and called from the same places, for
    /// the reason spelled out there: the view model is built once for the life of the window, so a
    /// snapshot taken in the constructor tells a user to install the model they have just
    /// installed. It also turns the opt-in <em>off</em> when the entry goes away, so a batch cannot
    /// start with the box ticked and nothing behind it.
    ///
    /// No format comes and goes with this one — <c>rttm</c> is the speaker opt-in's output and
    /// translation has no output of its own. What it has instead is a format it <em>forbids</em>,
    /// handled at Start, because <c>vtt-words</c> is a legitimate choice right up until the moment
    /// this box is ticked.
    /// </remarks>
    public void RefreshTranslationAvailability()
    {
        if (!_engines.SupportsTranslation && TranslateToEnglish)
        {
            TranslateToEnglish = false;
        }

        OnPropertyChanged(nameof(CanTranslate));
        OnPropertyChanged(nameof(TranslationHint));
    }

    /// <summary>
    /// Whether the selected row has an English transcript to switch to, which is what decides
    /// whether the pane switcher is drawn at all.
    /// </summary>
    public bool CanShowTranslation => SelectedJob?.HasTranslation ?? false;

    /// <summary>
    /// The lines the transcript area draws: the English ones when the switcher is on them and the
    /// row has them, the spoken ones otherwise.
    /// </summary>
    /// <remarks>
    /// The fallback is not defensive tidiness. The switcher is hidden on a row with no
    /// translation, so a person can leave it on English, click a file that was never translated,
    /// and be looking at a pane that no longer exists — which without this returns an empty
    /// collection and reads as a transcript that came back blank.
    /// </remarks>
    public IEnumerable<TranscriptLineViewModel>? VisibleLines =>
        SelectedJob is not { } job ? null
        : TranscriptPane == 1 && job.HasTranslation ? job.TranslatedLines
        : job.Lines;

    /// <summary>
    /// Re-asks what the transcript area should be showing, after the selected row's content
    /// changes underneath it.
    /// </summary>
    /// <remarks>
    /// Selecting a different row notifies through <see cref="SelectedJob"/>, but a row that
    /// finishes while it is already selected does not — the switcher would stay hidden on the
    /// transcript that had just been translated until the user clicked another file and back.
    /// Reset and Clear go the other way and are the same problem in reverse: a switcher still
    /// offering an English pane whose lines have just been thrown away.
    /// </remarks>
    private void RefreshTranscriptPane()
    {
        OnPropertyChanged(nameof(CanShowTranslation));
        OnPropertyChanged(nameof(VisibleLines));
    }

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

        // The queue is what the long-recording warning is computed over, so both move with it: a
        // three-hour file dropped onto a window whose opt-in is already on has to raise the warning
        // then, not at the next unrelated notification — and the hint beside the count field has to
        // stop saying the field can be left blank at the same moment Start begins to refuse it.
        OnPropertyChanged(nameof(SpeakerDurationWarning));
        OnPropertyChanged(nameof(SpeakerCountHint));
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

            Jobs.Add(new JobViewModel(path) { Duration = ProbeDuration(path) });
            added++;
        }

        RefreshQueueState();

        StatusMessage = rejected.Count == 0
            ? added == 0 ? "Those files are already in the queue." : $"Added {added} file{(added == 1 ? string.Empty : "s")}."
            : string.Join("; ", rejected);
    }

    /// <summary>
    /// How long a queued file is, or null when that cannot be answered from its header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opening a container reads its header and not its audio, which is the same thing
    /// <c>diarise</c> relies on to warn before it decodes a sample. What it buys here is the
    /// long-recording warning appearing when the file is dropped rather than twenty minutes into a
    /// batch, which is the difference between a warning somebody can act on and a note beside a
    /// finished transcript.
    /// </para>
    /// <para>
    /// A file that will not open is queued anyway with no duration. Being unreadable is a real
    /// result and the run reports it properly, per file, with the row marked failed and the rest of
    /// the batch untouched; refusing it here would turn one broken header into a file the user
    /// cannot even queue, and swallowing the error is right because this is advisory and nothing
    /// downstream depends on the answer.
    /// </para>
    /// </remarks>
    private static TimeSpan? ProbeDuration(string path)
    {
        try
        {
            var source = AudioSources.Open(path);
            try
            {
                return source.Duration;
            }
            finally
            {
                // Sources are IAsyncDisposable and this runs on the UI thread, so the wait is
                // guarded rather than taken: what is open here is a file handle over a header that
                // has already been read, and both readers complete their close synchronously. The
                // branch is the contract's, not this path's.
                var closing = source.DisposeAsync();
                if (!closing.IsCompleted)
                {
                    closing.AsTask().GetAwaiter().GetResult();
                }
            }
        }
#pragma warning disable CA1031 // A header that will not read is not a reason to refuse the file.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
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
        RefreshTranscriptPane();
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
        RefreshTranscriptPane();
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

        // Refused rather than degraded, which is the command line's rule too. Translation carries
        // no word timings — the English words are not the words that were spoken and nothing
        // aligns them — so a word-timed file written under this opt-in would highlight the wrong
        // word at every moment, and look entirely correct while doing it.
        if (TranslateToEnglish && formats.Contains(TranscriptFormats.WordTimedVtt.Id, StringComparer.Ordinal))
        {
            StatusMessage =
                $"'{TranscriptFormats.WordTimedVtt.DisplayName}' times every word, and translation does not carry "
                + "word timings. Drop that format, or turn the English version off and get the word timings of "
                + "what was actually said.";
            return;
        }

        // The same question the speaker opt-in asks, for the same reason: the Models tab can
        // remove the translation entry while this window is open, and a ticked box with nothing
        // behind it would write the source transcript into files named .en.
        if (TranslateToEnglish && !_engines.SupportsTranslation)
        {
            RefreshTranslationAvailability();
            StatusMessage = "The translation model is no longer installed. Download it again from the Models tab.";
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

        // Refused with a reason rather than thrown from Validate deep inside the batch. The field
        // cannot go below one, so this is the path a saved setting or a test takes; zero speakers is
        // not a smaller request than one, it is a different one, and blank already means "estimate".
        if (LabelSpeakers && SpeakerCount is { } requested && requested < 1)
        {
            StatusMessage = "The speaker count is how many people are talking, so it starts at one. "
                + "Leave it blank to let the model estimate.";
            return;
        }

        // Past the bound a blank count is not "let the model decide", it is "let the model do the one
        // thing it is measured to get wrong here" — over-segment one host into two labels, silently,
        // on a recording somebody is about to spend half an hour transcribing. So the batch stops
        // and asks.
        //
        // It stops rather than inventing a number, and that distinction is the whole design. The
        // fold merges whichever pair collides least whether or not the evidence supports it, so a
        // guessed count does not estimate the answer — it forces one, and puts two people under one
        // name with no margin behind the merge. Both ways out are decisions: give the number, or
        // take the transcript without names. Neither is a guess, and the words are unaffected by
        // either.
        if (LabelSpeakers && SpeakerCount is null && LongestPastTheBound() is { } risky)
        {
            StatusMessage =
                $"{risky.FileName} is longer than {EstablishedLength}, which is as far as this model's speaker "
                + "labels have been established, and past that it tends to hear one person as two. Set "
                + "'How many speakers', or turn 'Label speakers' off and take the transcript without names.";
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

            // One translator for the whole batch, on the same terms as the labeller: 1.34 GiB of
            // graphs loaded once rather than per file, and disposed with the batch.
            await using var translator = TranslateToEnglish && _engines.SupportsTranslation
                ? _engines.CreateTranslator()
                : null;

            if (TranslateToEnglish && translator is null)
            {
                StatusMessage = "The translation model is no longer installed. Download it again from the Models tab.";
                RefreshTranslationAvailability();
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

                // What makes a translated run's output its own. SubRip has no comment syntax, so
                // SRT cannot carry the marker in-band and is covered by its name instead — and the
                // infix is also what stops a translated run overwriting the transcription run's
                // files when both are asked for the same recording.
                StemSuffix = translator is null ? string.Empty : TranslatedInfix,
            }).ToList();

            foreach (var vm in pending)
            {
                vm.Reset();
            }

            RefreshTranscriptPane();

            // The user's count, carried into every file of the batch. Built once here rather than
            // per file so a batch cannot end up with two files labelled under different rules, and
            // built even when the opt-in is off, where it is never read.
            var speakerOptions = new SpeakerLabellingOptions { SpeakerCount = SpeakerCount };
            speakerOptions.Validate();

            var runner = new BatchTranscriptionRunner(
                (job, _, token) => RunJobAsync(engine, labeller, translator, job, options, speakerOptions, token));
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
        ITranscriptTranslator? translator,
        TranscriptionJob job,
        TranscriptionOptions options,
        SpeakerLabellingOptions speakerOptions,
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

            var merges = new List<string>();
            document = await SpeakerLabelling
                .LabelAsync(document, labeller, second, speakerOptions, progress, merges, ct)
                .ConfigureAwait(true);

            // Four sentences, widest first, and each about a different thing: what ran and whether
            // it reproduces the published figure, the recording being longer than the labels are
            // established on, the count merging these labels into those, and the labeller finishing
            // at its ceiling. The duration one is on screen before the batch started
            // (SpeakerDurationWarning) and repeated here because it belongs to the transcript too —
            // an options panel is not where somebody reads a result a week later.
            //
            // The backend sentence is first because it is the one that changes what every other
            // sentence is about: on a stack that does not reproduce the reference, the labels those
            // three describe are this machine's own. It comes from the provider because only the
            // provider knows what kind of labeller this is, and it is read here rather than at
            // Start because the provider is chosen inside the sidecar and is not known until load.
            speakerWarning = Join(
                Join(
                    _engines.DescribeLabeller(labeller),
                    Join(
                        SpeakerLabelling.DescribeDurationRisk(labeller.Capabilities, document.AudioDuration),
                        DescribeMerges(merges))),
                SpeakerLabelling.DescribeLimit(labeller, document));

            text.Clear();
            text.Append(JobViewModel.Render(document));
            Publish();
        }

        // Last, and after the speakers on purpose: SpeakerAssignment attributes a speaker per word
        // and cuts segments where the speaker changes, and a translated segment has no words. Run
        // the other way round it would fall back to "whoever talks most across the span" on every
        // segment — a coarser label, arrived at silently.
        //
        // The transcript as the engine wrote it is kept rather than replaced, which is what lets
        // the window offer both panes; it is also what the silence check below has to read, since
        // translation destroys the signal it rests on.
        var transcribed = document;
        string? numeralWarning = null;
        string? translatorWarning = null;

        if (translator is not null)
        {
            vm.Status = "Translating";
            document = await TranscriptTranslation
                .TranslateAsync(document, translator, progress: progress, ct: ct)
                .ConfigureAwait(true);

            // Dates and figures are what a listener checks a transcript for, and they are where a
            // two-model cascade meets worst. Compared against what was heard rather than against a
            // second reading of the English.
            numeralWarning = TranslationNumerals.Describe(transcribed.Segments, document.Segments);

            // What ran, on the same terms as the labeller's. The translator checks itself against a
            // committed reference at load and this window was running that check and discarding the
            // answer — which costs the check and delivers none of what it buys.
            translatorWarning = _engines.DescribeTranslator(translator);

            text.Clear();
            text.Append(JobViewModel.Render(document));
            Publish();
        }

        var written = await TranscriptWriter.WriteAsync(document, job, ct: ct).ConfigureAwait(true);

        var silence = DescribeSilence(engine, transcribed);
        var result = new JobResult
        {
            Job = job,
            State = JobState.Completed,
            Document = document,
            OutputFiles = written,
            Elapsed = DateTimeOffset.UtcNow - started,

            // Silence first, then the labeller at its cap, then what the translator's provider means
            // for the English, then a number the English lost: the file, the names, the whole
            // translation, one segment — widest first, as the command line orders them.
            Warning = Join(Join(Join(silence, speakerWarning), translatorWarning), numeralWarning),
        };

        vm.Complete(result, translator is null ? null : transcribed);
        RefreshTranscriptPane();
        return result;
    }

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : $"{first} {second}";

    /// <summary>
    /// What the speaker count actually merged, or null when it merged nothing.
    /// </summary>
    /// <remarks>
    /// A merge the user's own number asked for is still not a silent one, and each entry already
    /// carries the seconds the pair spent talking over each other <em>and</em> how far behind the
    /// next-closest pair was. The margin is what says whether the fold had a real choice to make:
    /// two hosts of a three-hour recording overlap for minutes however it is cut, so the absolute
    /// reads alarming on its own and means nothing without the runner-up beside it. Near-zero
    /// margin is a merge the count forced rather than one the timeline supports.
    /// </remarks>
    private static string? DescribeMerges(IReadOnlyList<string> merges) =>
        merges.Count == 0
            ? null
            : $"Folded to the speaker count you asked for: merged {string.Join("; ", merges)}. The margin is the "
              + "evidence rather than the raw seconds — two hosts of a long recording overlap for minutes however "
              + "you cut them, so what matters is how far behind the next-closest pair was. A merge with little or "
              + "no margin means the count has probably put two people under one name.";

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
