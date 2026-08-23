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
using Parakeet.Core.Muxing;
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
    private readonly Parakeet.App.Services.Tools.IMediaUrlFetcher _fetcher;
    private readonly Parakeet.App.Services.Tools.ISubtitleMuxer _muxer;
    private string? _addToRecordingResult;
    private readonly string _downloadRoot;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanRunAgain))]
    [NotifyPropertyChangedFor(nameof(StartHint))]
    [NotifyPropertyChangedFor(nameof(DropHint))]
    [NotifyPropertyChangedFor(nameof(CanFetchUrl))]
    [NotifyPropertyChangedFor(nameof(CanAddToRecording))]
    [NotifyPropertyChangedFor(nameof(AddToRecordingNotice))]
    [NotifyPropertyChangedFor(nameof(CanExportFiles))]
    [NotifyPropertyChangedFor(nameof(ExportNotice))]
    [NotifyCanExecuteChangedFor(nameof(FetchUrlCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddToRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportFilesCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportNotice))]
    private string? _outputDirectory;

    /// <summary>The link in the box, as it is typed or pasted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFetchUrl))]
    [NotifyCanExecuteChangedFor(nameof(FetchUrlCommand))]
    private string? _url;

    /// <summary>Whether a fetch is in flight, which is what shuts the box while it runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFetchUrl))]
    [NotifyCanExecuteChangedFor(nameof(FetchUrlCommand))]
    private bool _isFetchingUrl;

    /// <summary>What the fetch is doing, or why it failed. Null when there is nothing to say.</summary>
    [ObservableProperty]
    private string? _urlStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleLines))]
    [NotifyPropertyChangedFor(nameof(CanShowTranslation))]
    [NotifyPropertyChangedFor(nameof(CanAddToRecording))]
    [NotifyPropertyChangedFor(nameof(AddToRecordingNotice))]
    [NotifyPropertyChangedFor(nameof(CanExportFiles))]
    [NotifyPropertyChangedFor(nameof(ExportNotice))]
    [NotifyCanExecuteChangedFor(nameof(AddToRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportFilesCommand))]
    private JobViewModel? _selectedJob;

    /// <summary>A tick under Output formats changes what Export writes and what would go inside the recording.</summary>
    private void OnFormatChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ExportableFormat));
        OnPropertyChanged(nameof(CanAddToRecording));
        OnPropertyChanged(nameof(AddToRecordingNotice));
        OnPropertyChanged(nameof(CanExportFiles));
        OnPropertyChanged(nameof(ExportNotice));
        AddToRecordingCommand.NotifyCanExecuteChanged();
        ExportFilesCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedJobChanged(JobViewModel? value)
    {
        // The results of the last presses belonged to the row they ran on. Left standing under a
        // different recording they read as claims about this one.
        _addToRecordingResult = null;
        _exportResult = null;
        OnPropertyChanged(nameof(AddToRecordingNotice));
        OnPropertyChanged(nameof(ExportNotice));
    }

    [ObservableProperty]
    private string _liveTranscript = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _useFixedWindows;

    /// <summary>
    /// Cut the audio on a neural speech detector rather than the loudness gate. <b>On by default</b>
    /// — the maintainer's call, later on 2026-08-23, on the documentary that raised it — and unlike
    /// the two sidecar opt-ins, which stay off: a detector that hears pauses under music is what a
    /// person with a recording wants, and the command line took the same default later that day,
    /// with every transcript's JSON naming what cut it so a figure keeps its method. The box follows
    /// the model: <see cref="RefreshSpeechDetectionAvailability"/> unticks it when the model is not
    /// there and ticks it when the model arrives, unless the user has answered the box themselves.
    /// </summary>
    [ObservableProperty]
    private bool _useNeuralSpeechDetection = true;

    /// <summary>
    /// The user's own answer to the detection box, or null while it is still the default. Kept
    /// apart from the property because this window also writes the property when the model comes
    /// or goes, and a choice the user made should survive that: untick it, remove the model from
    /// the Models tab, install it again, and it comes back unticked. Set from the property's change
    /// hook, so the window's own writes are fenced off by <see cref="_settingSpeechDetection"/>.
    /// </summary>
    private bool? _speechDetectionChoice;

    private bool _settingSpeechDetection;

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
    /// How many people are talking. Null is the field not yet filled in, and with the opt-in on
    /// that is a state Start refuses rather than a request to estimate: this window requires the
    /// number.
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
    /// Required rather than optional, and not because the estimate always fails — inside the
    /// established bound it is measured correct. It is required because when the estimate fails it
    /// fails silently: a drifted host arrives as a plausible extra speaker, nothing in the output
    /// says which of "four people" and "one person heard twice" happened, and the one person who
    /// trivially knows the real number was never asked. The command line keeps the blank-and-
    /// estimate path, which is what the measurements run through.
    /// </para>
    /// <para>
    /// Still blank rather than defaulting to two, because the number has to come from the user for
    /// the fold to mean anything. The fold is wrong to apply unasked: on one of the eighteen AMI
    /// development meetings the pair of <em>genuinely different</em> speakers who collide least
    /// never overlap at all across the whole meeting, so a guessed default would merge two real
    /// people there and stamp it with a margin. It fires only when somebody says they know.
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
        IEngineProvider engines,
        Func<EngineSelection> selection,
        ModelSession? session = null,
        Parakeet.App.Services.Tools.IMediaUrlFetcher? fetcher = null,
        string? downloadRoot = null,
        Parakeet.App.Services.Tools.ISubtitleMuxer? muxer = null)
    {
        ArgumentNullException.ThrowIfNull(engines);
        ArgumentNullException.ThrowIfNull(selection);

        _engines = engines;
        _selection = selection;
        _session = session;
        _fetcher = fetcher ?? new Parakeet.App.Services.Tools.YtDlpMediaUrlFetcher();
        _muxer = muxer ?? new Parakeet.App.Services.Tools.FfmpegSubtitleMuxer();

        // Under the user's temp directory rather than beside the application: these are working
        // copies of somebody else's media, they can be large, and nothing downstream keeps them.
        _downloadRoot = downloadRoot ?? Path.Combine(Path.GetTempPath(), "Uindosill", "links");

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
            .Select(f => new OutputFormatViewModel(f, f.Id is "srt"))];

        // Which format goes inside the recording is read off these ticks, so the button beside them
        // has to hear one change. RTTM comes and goes with the diariser, so the collection is
        // followed as well as its current contents.
        Formats.CollectionChanged += (_, e) =>
        {
            foreach (var format in e.NewItems?.OfType<OutputFormatViewModel>() ?? [])
            {
                format.PropertyChanged += OnFormatChanged;
            }

            foreach (var format in e.OldItems?.OfType<OutputFormatViewModel>() ?? [])
            {
                format.PropertyChanged -= OnFormatChanged;
            }

            OnFormatChanged(null, new System.ComponentModel.PropertyChangedEventArgs(null));
        };

        foreach (var format in Formats)
        {
            format.PropertyChanged += OnFormatChanged;
        }

        RefreshSpeakerAvailability();
        RefreshTranslationAvailability();
        RefreshSpeechDetectionAvailability();
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
    /// Whether the checkbox for the neural speech-detection opt-in does anything, on the same terms
    /// as the speaker one: disabled with <see cref="SpeechDetectionHint"/> beside it when it would
    /// silently do nothing.
    /// </summary>
    public bool CanUseNeuralSpeechDetection => _engines.SupportsNeuralSpeechDetection;

    /// <summary>
    /// Why the detection opt-in is off or inert, or null when it is live. Two cases: the model is
    /// not installed, which the provider says; or fixed windows are on, which this window knows —
    /// a detector decides nothing under fixed windows, and a ticked box that changes nothing is the
    /// silently-inert setting this window refuses to draw.
    /// </summary>
    public string? SpeechDetectionHint =>
        !CanUseNeuralSpeechDetection ? _engines.DescribeUnavailable(ModelTask.VoiceActivity)
        : UseFixedWindows ? "Fixed windows are on, so no detector runs — untick that to cut on speech."
        : null;

    /// <summary>
    /// Re-asks whether a speech detector is available and brings the checkbox and its hint into
    /// line — the third of these, called from the same places as the other two and for the reason
    /// spelled out on <see cref="RefreshSpeakerAvailability"/>. The box follows the model: off when
    /// it is not there, so a batch cannot start with the box ticked and nothing behind it; on when
    /// it is, because that is the default — unless the user has answered the box themselves, in
    /// which case their answer is restored rather than the default.
    /// </summary>
    public void RefreshSpeechDetectionAvailability()
    {
        _settingSpeechDetection = true;
        try
        {
            UseNeuralSpeechDetection = _engines.SupportsNeuralSpeechDetection && (_speechDetectionChoice ?? true);
        }
        finally
        {
            _settingSpeechDetection = false;
        }

        OnPropertyChanged(nameof(CanUseNeuralSpeechDetection));
        OnPropertyChanged(nameof(SpeechDetectionHint));
    }

    // A write that did not come from RefreshSpeechDetectionAvailability is the user's answer.
    partial void OnUseNeuralSpeechDetectionChanged(bool value)
    {
        if (!_settingSpeechDetection)
        {
            _speechDetectionChoice = value;
        }
    }

    // The hint reads the fixed-windows box, so it moves when that box does.
    partial void OnUseFixedWindowsChanged(bool value) => OnPropertyChanged(nameof(SpeechDetectionHint));

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
                // Blank is not an option while the opt-in is on, and the hint says so here rather
                // than let Start be the first place anybody hears it. One sentence for every queue:
                // the long-recording escalation is SpeakerDurationWarning's job, drawn right beside
                // this field.
                return "Give the number of people talking; 'Label speakers' does not run without it. "
                    + "The model's own estimate can silently hear one person as two on long recordings, "
                    + "so the count is always asked for.";
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
    /// Re-asks whether there are weights for Start to load, after the Models tab has installed or
    /// removed the transcription entry.
    /// </summary>
    /// <remarks>
    /// The third of these, and it exists for the same reason as the other two: this view model is
    /// built once for the life of the window, so anything it answers from disk goes stale the
    /// moment the tab next door changes what is on it. Without it Start stays dark after a
    /// download finishes, which reads as a broken button beside a model the tab says is installed.
    /// </remarks>
    public void RefreshModelAvailability()
    {
        OnPropertyChanged(nameof(IsModelInstalled));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StartHint));
    }

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
    /// Which format would go inside the recording, or null when none of the selected ones can.
    /// </summary>
    /// <remarks>
    /// Read off the formats already chosen rather than asked for again — somebody who ticked
    /// "WebVTT (words)" has said what they want their transcript to be, and asking a second time at
    /// the moment they press a button is asking them to say it twice.
    ///
    /// Richest first, then cheapest container. A word-timed WebVTT carries everything a plain one
    /// does and the word times as well, so where it is selected it is the one worth putting in —
    /// even though it is also the one that forces Matroska, which is why
    /// <see cref="AddToRecordingNotice"/> says so before anything is written. Between a plain
    /// WebVTT and an SRT there is nothing to choose on content, so the tie goes to SRT: it keeps
    /// the file an MP4, and a plain WebVTT would force Matroska for no gain at all.
    /// </remarks>
    public string? ExportableFormat =>
        new[] { "vtt-words", "srt", "vtt" }
            .FirstOrDefault(id => Formats.Any(f => f.IsSelected && f.Id == id));

    /// <summary>Whether the transcript of the open recording can be put inside it.</summary>
    public bool CanAddToRecording =>
        !IsRunning
        && SelectedJob is { CanExport: true } job
        && !job.IsFromUrl
        && ExportableFormat is not null
        && _muxer.IsAvailable
        && SubtitleMux.TryPlan(job.Path, ExportableFormat, out _, out _);

    /// <summary>
    /// What pressing it will do, or why it cannot be pressed. Never null while a row is selected:
    /// a disabled control with no reason beside it is the defect this window keeps finding.
    /// </summary>
    public string? AddToRecordingNotice
    {
        get
        {
            if (_addToRecordingResult is { } result)
            {
                return result;
            }

            // Not null any more, and the move to the Export tab is why. This notice used to sit
            // under a button on the same screen as the queue, so "nothing is selected" explained
            // itself — the empty list was right there. On a page of its own the button is simply
            // dark with nothing beside it, which is the shape of every interface defect this
            // window has shipped.
            if (SelectedJob is not { } job)
            {
                return "Choose a recording in the queue on the Transcribe tab; this writes the transcript of "
                    + "whichever one is highlighted there.";
            }

            if (_muxer.DescribeUnavailable() is { } unavailable)
            {
                return unavailable;
            }

            if (!job.CanExport)
            {
                return "Transcribe this recording first.";
            }

            if (job.IsFromUrl)
            {
                return "This one came from a link, so the recording here is the audio that was "
                    + "downloaded. Its transcript sits beside it as a file.";
            }

            if (ExportableFormat is not { } format)
            {
                return "Tick SRT or WebVTT under Output formats, on the Transcribe tab — those are the two a "
                    + "recording can carry.";
            }

            if (!SubtitleMux.TryPlan(job.Path, format, out var plan, out var refusal))
            {
                return refusal;
            }

            var name = Path.GetFileName(plan.OutputPath);

            return plan.Note is { } note
                ? $"Writes {name}. {note}"
                : $"Writes {name} beside the original, which is left alone.";
        }
    }

    /// <summary>
    /// Puts the transcript inside the recording, as a subtitle track, in a new file beside it.
    /// </summary>
    /// <remarks>
    /// The transcript is rendered here rather than taken off disk, which is what makes a speaker
    /// somebody renamed reach the file: the sidecars were written before the window had a chance to
    /// show anyone a name. See <c>JobViewModel.Named</c>.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanAddToRecording))]
    private async Task AddToRecordingAsync(CancellationToken cancellationToken)
    {
        if (SelectedJob is not { } job
            || ExportableFormat is not { } formatId
            || job.Named() is not { } document
            || !SubtitleMux.TryPlan(job.Path, formatId, out var plan, out _))
        {
            return;
        }

        _addToRecordingResult = null;
        OnPropertyChanged(nameof(AddToRecordingNotice));

        // Beside the transcript the run already wrote, and thrown away afterwards: what belongs to
        // the user is the media file, and a second copy of a transcript they already have is litter.
        var scratch = Path.Combine(
            Path.GetTempPath(), "uindosill-mux-" + Guid.NewGuid().ToString("N")[..12]
                + TranscriptFormats.Get(formatId).FileExtension);

        try
        {
            await File.WriteAllTextAsync(
                scratch, TranscriptFormats.Get(formatId).Format(document, null), cancellationToken)
                .ConfigureAwait(true);

            var written = await _muxer
                .MuxAsync(plan, scratch, null, cancellationToken)
                .ConfigureAwait(true);

            job.OutputFiles.Add(written);
            _addToRecordingResult = $"Added the transcript to {Path.GetFileName(written)}.";
        }
        catch (OperationCanceledException)
        {
            _addToRecordingResult = null;
        }
        catch (Exception ex) when (ex is Parakeet.App.Services.Tools.SubtitleMuxException or IOException)
        {
            _addToRecordingResult = ex.Message;
        }
        finally
        {
            try
            {
                if (File.Exists(scratch))
                {
                    File.Delete(scratch);
                }
            }
            catch (IOException)
            {
            }

            OnPropertyChanged(nameof(AddToRecordingNotice));
        }
    }

    /// <summary>What the last press of Export did, cleared when the selection moves on.</summary>
    private string? _exportResult;

    /// <summary>Whether Export is live: a finished recording is selected and a format is ticked.</summary>
    public bool CanExportFiles =>
        !IsRunning
        && SelectedJob is { CanExport: true }
        && Formats.Any(f => f.IsSelected);

    /// <summary>
    /// What pressing Export will do, why it cannot be pressed, or what it just did. Never null:
    /// a disabled control with no reason beside it is the defect this window keeps finding.
    /// </summary>
    public string ExportNotice
    {
        get
        {
            if (_exportResult is { } result)
            {
                return result;
            }

            if (SelectedJob is not { } job)
            {
                return "Choose a recording in the queue on the Transcribe tab; this writes the files of "
                    + "whichever one is highlighted there.";
            }

            if (!job.CanExport)
            {
                return "Transcribe this recording first.";
            }

            var ticked = Formats.Count(f => f.IsSelected);
            if (ticked == 0)
            {
                return "Tick at least one format above.";
            }

            var where = string.IsNullOrWhiteSpace(OutputDirectory)
                ? "beside the recording"
                : $"in {OutputDirectory}";
            var english = job.TranslatedDocument is not null
                ? $", and the English beside them as {TranslatedInfix} files"
                : string.Empty;

            return $"Writes {job.DisplayName}'s transcript as {ticked} file{(ticked == 1 ? string.Empty : "s")} {where}{english}.";
        }
    }

    /// <summary>
    /// Writes the ticked formats for the selected recording — the spoken transcript under its
    /// current speaker names, and the English beside it when the run translated.
    /// </summary>
    /// <remarks>
    /// Transcribing writes nothing since later on 2026-08-23; this button is the one place files
    /// come from. The refusals Start used to make are skips with reasons here, because the
    /// finished document answers what Start could only predict: a turns-only format over a
    /// transcript with no turns writes nothing and says why, and the English gets no word-timed
    /// file because translation carries no word timings — the English words are not the words
    /// that were spoken and nothing aligns them.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanExportFiles))]
    private async Task ExportFilesAsync(CancellationToken cancellationToken)
    {
        if (SelectedJob is not { } job || job.Named() is not { } document)
        {
            return;
        }

        // The command guards itself as well as its CanExecute, because ExecuteAsync runs from a
        // shortcut or a test whether or not the button is lit.
        var ticked = Formats.Where(f => f.IsSelected).Select(f => f.Id).ToList();
        if (ticked.Count == 0)
        {
            return;
        }

        _exportResult = null;
        OnPropertyChanged(nameof(ExportNotice));

        var outputDirectory = string.IsNullOrWhiteSpace(OutputDirectory) ? null : OutputDirectory;

        try
        {
            var written = new List<string>();
            var skipped = new List<string>();

            var spokenFormats = ticked;
            if (ticked.Contains(TranscriptFormats.Rttm.Id, StringComparer.Ordinal)
                && document.SpeakerTurns is not { Count: > 0 })
            {
                spokenFormats = ticked.Where(id => id != TranscriptFormats.Rttm.Id).ToList();
                skipped.Add("no RTTM, because this transcript has no speaker turns and the file would be empty");
            }

            written.AddRange(await TranscriptWriter.WriteAsync(document, new TranscriptionJob
            {
                InputPath = job.Path,
                Formats = spokenFormats,
                OutputDirectory = outputDirectory,
            }, ct: cancellationToken).ConfigureAwait(true));

            if (job.NamedTranslation() is { } english)
            {
                // No second RTTM either: the turns are the spoken document's fact and that file
                // is already written above.
                var englishFormats = ticked
                    .Where(id => id != TranscriptFormats.WordTimedVtt.Id && id != TranscriptFormats.Rttm.Id)
                    .ToList();

                if (ticked.Contains(TranscriptFormats.WordTimedVtt.Id, StringComparer.Ordinal))
                {
                    skipped.Add("no word-timed English, because translation does not carry word timings — that file describes the spoken transcript only");
                }

                written.AddRange(await TranscriptWriter.WriteAsync(english, new TranscriptionJob
                {
                    InputPath = job.Path,
                    Formats = englishFormats,
                    OutputDirectory = outputDirectory,
                    StemSuffix = TranslatedInfix,
                }, ct: cancellationToken).ConfigureAwait(true));
            }

            job.OutputFiles.AddRange(written);

            var where = outputDirectory is null ? "beside the recording" : $"in {outputDirectory}";
            _exportResult = $"Wrote {written.Count} file{(written.Count == 1 ? string.Empty : "s")} {where}"
                + (skipped.Count > 0 ? $" — {string.Join("; ", skipped)}." : ".");
        }
        catch (OperationCanceledException)
        {
            _exportResult = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _exportResult = ex.Message;
        }
        finally
        {
            OnPropertyChanged(nameof(ExportNotice));
        }
    }

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

    /// <summary>
    /// Whether there are weights on disk for Start to load if none are resident.
    /// </summary>
    /// <remarks>
    /// Asked of the store through the provider on every evaluation rather than cached, on the same
    /// terms as the two opt-ins: the Models tab can install or remove an entry while this tab is
    /// open, and the answer is a file's existence rather than anything expensive.
    /// </remarks>
    public bool IsModelInstalled => _engines.IsModelAvailable(_selection());

    /// <summary>An enabled Start button with an empty queue does nothing when pressed, which reads
    /// as a broken button rather than an empty queue. The same is true of a Start with every file
    /// already transcribed, and of a Start with no weights anywhere to run, so all three are
    /// disabled here rather than failing at the press.</summary>
    /// <remarks>
    /// A model that is installed but not <em>loaded</em> no longer disables this. Start loads it —
    /// see <see cref="StartAsync"/> — because requiring a visit to another tab to press a second
    /// button before the obvious one works is a step that exists for the implementation's benefit
    /// rather than the user's. It is deliberately here and not at launch: loading fixes the compute
    /// backend for the rest of the process, so doing it on startup would take the backend choice
    /// away from somebody who had not asked for anything yet.
    /// </remarks>
    public bool CanStart => !IsRunning && HasWorkToDo && (IsModelLoaded || IsModelInstalled);

    /// <summary>
    /// Says why Start is off when the reason is not simply an empty queue. A disabled button with
    /// no explanation is the same dead end as a button that does nothing — and "everything here is
    /// already transcribed" is the reason a person is least likely to guess, because the queue in
    /// front of them is full.
    /// </summary>
    public string? StartHint =>
        !IsModelLoaded && !IsModelInstalled ? "No model is installed — open the Models tab and download one."
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

    // ── Adding a link ─────────────────────────────────────────────────────────────────────────
    //
    // Paste a link and the audio track is downloaded and queued like a file. Audio alone, because
    // the transcript is made from sound: a three-hour video costs a few megabytes here rather than
    // a few gigabytes, and the Ask tab streams the picture from the link on demand instead of
    // keeping a copy of it.

    /// <summary>Whether this build can fetch links at all.</summary>
    public bool CanAddUrl => _fetcher.IsAvailable;

    /// <summary>Why it cannot, or null when it can.</summary>
    public string? UrlHint => _fetcher.DescribeUnavailable();

    /// <summary>Whether the button is live: something pasted, not already fetching, not running.</summary>
    public bool CanFetchUrl =>
        CanAddUrl && !IsFetchingUrl && !IsRunning && !string.IsNullOrWhiteSpace(Url);

    /// <summary>
    /// Fetches the pasted link's audio and queues it.
    /// </summary>
    /// <remarks>
    /// The row is added only once the download has finished. A row that appeared first and filled
    /// in afterwards would be a queue entry whose file does not exist yet, and every other thing
    /// that reads the queue — the duration probe, the Ask tab's player, Start — would have to learn
    /// about a state that exists for no other row.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanFetchUrl))]
    private async Task FetchUrlAsync(CancellationToken cancellationToken)
    {
        var url = Url?.Trim();

        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        IsFetchingUrl = true;
        UrlStatus = "Reading the link";

        try
        {
            Directory.CreateDirectory(_downloadRoot);

            var progress = new Progress<Parakeet.App.Services.Tools.UrlFetchProgress>(report =>
            {
                UrlStatus = report.Fraction is { } fraction
                    ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{report.Stage} — {fraction * 100:F0}%")
                    : report.Stage;
            });

            var fetched = await _fetcher
                .FetchAudioAsync(url, _downloadRoot, progress, cancellationToken)
                .ConfigureAwait(true);

            if (Jobs.Any(j => string.Equals(j.SourceUrl, fetched.SourceUrl, StringComparison.OrdinalIgnoreCase)))
            {
                UrlStatus = "That link is already in the queue.";
                return;
            }

            Jobs.Add(new JobViewModel(fetched.Path)
            {
                Duration = ProbeDuration(fetched.Path),
                SourceUrl = fetched.SourceUrl,
                DisplayName = fetched.Title,
            });

            Url = string.Empty;
            UrlStatus = null;
            StatusMessage = $"Added “{fetched.Title}” from the link.";
            RefreshQueueState();
        }
        catch (OperationCanceledException)
        {
            UrlStatus = "Cancelled.";
        }
        catch (Parakeet.App.Services.Tools.MediaFetchException ex)
        {
            UrlStatus = ex.Message;
        }
        finally
        {
            IsFetchingUrl = false;
        }
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
            //
            // Nothing loaded is no longer a refusal. Until 2026-08-23 this told the user to go to
            // the Models tab and press Load, which is a second button for a decision they had
            // already made by pressing this one: there is exactly one transcription entry, and
            // wanting it loaded is the only reason to press Start. What it is *not* is a load at
            // launch — the backend cannot be changed once any model is resident, so the load waits
            // for the moment somebody actually asks for work.
            if (!_session.IsLoaded)
            {
                if (!_engines.IsModelAvailable(selection))
                {
                    StatusMessage = "No model is installed. Open the Models tab and download one first.";
                    return;
                }

                StatusMessage = "Loading the model…";

                try
                {
                    await _session.LoadAsync(selection).ConfigureAwait(true);
                }
#pragma warning disable CA1031 // A load failure belongs in the status line, not in a crash dialog.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    StatusMessage = exception.Message;
                    return;
                }

                if (!_session.IsLoaded)
                {
                    StatusMessage = "The model did not load.";
                    return;
                }

                StatusMessage = null;
            }
        }
        else if (!_engines.IsModelAvailable(selection))
        {
            StatusMessage = "No model is installed. Open the Models tab and download one first.";
            return;
        }

        // No format questions at Start, and no files either — since later on 2026-08-23 a run
        // fills the transcript on screen and nothing else, and the Export tab's button is what
        // writes files, from the ticks beside it, for the recording selected in the queue. The
        // refusals that used to stand here — a turns-only format with no speaker opt-in, a
        // word-timed format over a translation — moved with the writing, into the export path
        // that can now see the finished document instead of predicting it.

        // The Models tab can remove the translation entry while this window is open, and a ticked
        // box with nothing behind it would run a batch that quietly delivers no English.
        if (TranslateToEnglish && !_engines.SupportsTranslation)
        {
            RefreshTranslationAvailability();
            StatusMessage = "The translation model is no longer installed. Download it again from the Models tab.";
            return;
        }

        // The diariser is a file on disk and can go away between opening the window and pressing
        // Start — the Models tab will remove it, since only the *loaded* transcription engine is
        // protected there. Asked again here rather than trusted from construction, because the
        // alternative is a transcript that was asked for names and comes back without them.
        if (LabelSpeakers && !_engines.SupportsSpeakerLabelling)
        {
            RefreshSpeakerAvailability();
            StatusMessage = "The speaker labelling model is no longer installed. Download it again from the Models tab.";
            return;
        }

        // Refused with a reason rather than thrown from Validate deep inside the batch. The field
        // cannot go below one, so this is the path a saved setting or a test takes; zero speakers is
        // not a smaller request than one, it is a different one.
        if (LabelSpeakers && SpeakerCount is { } requested && requested < 1)
        {
            StatusMessage = "The speaker count is how many people are talking, so it starts at one.";
            return;
        }

        // The opt-in does not run without a count, whatever the queue holds. It began as a
        // past-the-bound rule, and what widened it is what the estimate's failure looks like from
        // the outside: a drifted host arrives as a plausible extra speaker, nothing in the output
        // says which of "four people" and "one person heard twice" happened, and an archived
        // transcript carries no trace of the difference. The person pressing Start trivially knows
        // the number; the model, when it is wrong, does not know that it is.
        //
        // It stops rather than inventing a number, and that distinction is the whole design. The
        // fold merges whichever pair collides least whether or not the evidence supports it, so a
        // guessed count does not estimate the answer — it forces one, and puts two people under one
        // name with no margin behind the merge. Both ways out are decisions: give the number, or
        // take the transcript without names. Neither is a guess, and the words are unaffected by
        // either. A file past the bound still gets the sentence that names it, because "this
        // specific recording is where the estimate is measured to go wrong" is more actionable
        // than the rule alone.
        if (LabelSpeakers && SpeakerCount is null)
        {
            StatusMessage = LongestPastTheBound() is { } risky
                ? $"{risky.FileName} is longer than {EstablishedLength}, which is as far as this model's speaker "
                    + "labels are reliable — past that it can hear one person as two. Set "
                    + "'How many speakers' under the opt-in, or turn 'Label speakers' off and take the transcript "
                    + "without names."
                : "'Label speakers' needs to know how many. Set 'How many speakers' under the opt-in, or turn "
                    + "it off and take the transcript without names.";
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

            // The speech detector for the whole batch, on the labeller's terms below: created only
            // when the opt-in is on and the provider has one to give, disposed with the batch. Fixed
            // windows make a detector decide nothing, so none is loaded under them. A graph on disk
            // that will not load is a sentence here rather than a silent fall-back to the gate — a
            // transcript cut by the gate when the box said detector would be provenance nobody could
            // read off it.
            ISpeechDetector? speechDetector = null;
            if (UseNeuralSpeechDetection && !UseFixedWindows)
            {
                if (!_engines.SupportsNeuralSpeechDetection)
                {
                    StatusMessage = "The speech detection model is no longer installed. Download it again from the Models tab.";
                    RefreshSpeechDetectionAvailability();
                    return;
                }

                try
                {
                    speechDetector = _engines.CreateSpeechDetector();
                }
                catch (SpeechDetectorException exception)
                {
                    StatusMessage = exception.Message;
                    return;
                }
            }

            using var detectorLifetime = speechDetector;
            var options = BuildOptions() with { SpeechDetector = speechDetector };

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

            // Loaded here, before a file is decoded, on the command line's terms (LabellerFactory
            // and TranslatorFactory do the same): a bundled Python that will not start, a
            // checkpoint that will not load, a provider that refuses — each is a sentence in the
            // status bar now, rather than a failed row after the first file's full decode and
            // again after every other file's, which is what loading inside the pass cost until
            // 2026-08-22. It also means the labeller's capabilities are real from here on, so the
            // backend and parity sentences read off a loaded engine rather than a guess at one.
            if (labeller is not null)
            {
                StatusMessage = "Loading the speaker labelling model…";
                await labeller.LoadAsync(ct).ConfigureAwait(true);
            }

            if (translator is not null)
            {
                StatusMessage = "Loading the translation model…";
                await translator.LoadAsync(ct).ConfigureAwait(true);
            }

            StatusMessage = null;

            // What has not been transcribed, which is not the same as what is in the queue. A row
            // that finished keeps its transcript, its outputs and its "Done"; the alternative is
            // that adding a fourth file to a queue of three re-decodes the three, which costs
            // minutes a file and leaves 'name (2).txt' beside every original. Failed and cancelled
            // rows are in here: pressing Start after a failure is how a person retries one.
            var pending = Jobs.Where(vm => vm.State != JobState.Completed).ToList();
            var alreadyDone = Jobs.Count - pending.Count;

            // Empty formats is the writer's documented "in memory only": a run produces the
            // transcript on screen and nothing on disk, and the Export tab's button does the
            // writing afterwards from the finished row.
            var jobs = pending.Select(vm => new TranscriptionJob
            {
                InputPath = vm.Path,
                Formats = [],
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

            // A file written without a pass it asked for is finished and is not what was asked
            // for, and a summary that said only "finished" would be the first to hide it. The row
            // carries the reason; this names what is missing so that "Finished 3 files" cannot be
            // read as three files with speakers.
            var incomplete = results.Where(r => r.State == JobState.Completed && r.FailedPasses.Count > 0).ToList();
            if (incomplete.Count > 0)
            {
                var missing = incomplete.SelectMany(r => r.FailedPasses).Select(f => f.Pass.Product).Distinct();
                ran += $" {incomplete.Count} of them written without {string.Join(" or ", missing)} — the row says why.";
            }

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

        // How many of `segments` have been turned into rows in the pane. The pane draws
        // JobViewModel.Lines, and until 2026-08-23 that collection was filled in one go by
        // Complete() — so the transcript area stayed empty for the whole decode and the streamed
        // text went into LiveTranscript, which no view has ever bound. A file being transcribed
        // showed a progress bar and nothing to read.
        var lined = 0;

        void Publish()
        {
            vm.Transcript = text.ToString();

            // Appended rather than rebuilt: the collection is what the pane is bound to, and
            // clearing it every 250 ms would scroll the reader back to the top of a transcript they
            // are in the middle of. No speaker and no chip — a speaker is what the opt-in pass
            // decides afterwards, and Complete() rebuilds these rows with the names on them. The
            // rows are cut the way Complete() will cut them — one per sentence, through the same
            // factory — so a transcript does not re-break itself the moment the decode ends.
            for (; lined < segments.Count; lined++)
            {
                var segment = segments[lined];
                if (segment.IsEmpty)
                {
                    continue;
                }

                foreach (var line in TranscriptLineViewModel.LinesFor(segment, voice: null))
                {
                    vm.Lines.Add(line);
                }
            }

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
            DecodeTime = (engine as SegmentingTranscriptionEngine)?.LastDecodeDuration,
            SpeechDetector = (engine as SegmentingTranscriptionEngine)?.LastSegmentationReport?.SpeechDetector,
        };

        // Either opt-in pass can fail where the transcript did not, and when one does the transcript
        // is written and shown without it and the row says so — the command line's rule, for the
        // command line's reason: the words were waited for, the pass is a decoration of them, and a
        // row marked Failed over a finished decode is that decode thrown away.
        var failures = new List<PassFailure>();

        string? speakerWarning = null;
        if (labeller is not null)
        {
            // The second pass. Both audio sources are single-read, so the file is opened again;
            // the labelled transcript then replaces the streamed one in the window, names in front.
            //
            // BeginPass rather than a status assignment: the bar is still full from the decode that
            // has just finished, and leaving it there under a new status is a finished-looking row
            // that then does not move for as long as this pass takes.
            vm.BeginPass("Labelling speakers");

            async Task<TranscriptDocument> LabelAsync()
            {
                await using var second = AudioSources.Open(job.InputPath);
                return await SpeakerLabelling
                    .LabelAsync(document, labeller, second, speakerOptions, progress, ct)
                    .ConfigureAwait(true);
            }

            var (labelled, failure) = await OptInPass.Speakers.RunAsync(document, LabelAsync).ConfigureAwait(true);

            if (failure is null)
            {
                document = labelled;

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
                            DescribeMerges(document.SpeakerFolds))),
                    SpeakerLabelling.DescribeLimit(labeller, document));

                text.Clear();
                text.Append(JobViewModel.Render(document));
                Publish();
            }
            else
            {
                failures.Add(failure);
            }
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
        var translated = false;
        string? numeralWarning = null;
        string? translatorWarning = null;

        if (translator is not null)
        {
            vm.BeginPass("Translating");

            var (english, failure) = await OptInPass.Translation.RunAsync(
                document,
                () => TranscriptTranslation.TranslateAsync(document, translator, progress: progress, ct: ct))
                .ConfigureAwait(true);

            if (failure is null)
            {
                document = english;
                translated = true;

                // Dates and figures are what a listener checks a transcript for, and they are where a
                // two-model cascade meets worst. Compared against what was heard rather than against a
                // second reading of the English.
                // Against the sentences the translator was given, not the segments: the English
                // pairs with those by index since it is translated a sentence at a time.
                numeralWarning = TranslationNumerals.Describe(TranscriptTranslation.Units(transcribed), document.Segments);

                // What ran, on the same terms as the labeller's. The translator checks itself against a
                // committed reference at load and this window was running that check and discarding the
                // answer — which costs the check and delivers none of what it buys.
                translatorWarning = _engines.DescribeTranslator(translator);

                text.Clear();
                text.Append(JobViewModel.Render(document));
                Publish();
            }
            else
            {
                failures.Add(failure);
            }
        }

        // Written as what it is — the spoken transcript under the plain name when the translation
        // failed, and with no turns-only format when the speakers did.
        var written = await TranscriptWriter.WriteAsync(document, job.WithoutFailedPasses(failures), ct: ct)
            .ConfigureAwait(true);

        var silence = Join(DescribeSilence(engine, transcribed), DescribeUnsegmented(engine, transcribed));
        var result = new JobResult
        {
            Job = job,
            State = JobState.Completed,
            Document = document,
            OutputFiles = written,
            Elapsed = DateTimeOffset.UtcNow - started,
            FailedPasses = failures,

            // Silence first, then the labeller at its cap, then what the translator's provider means
            // for the English, then a number the English lost: the file, the names, the whole
            // translation, one segment — widest first, as the command line orders them.
            Warning = Join(Join(Join(silence, speakerWarning), translatorWarning), numeralWarning),
        };

        // The pane switcher is drawn for a row that has both documents; a row whose translation
        // failed has one, so it gets no switcher rather than two panes of the same text.
        vm.Complete(result, translated ? transcribed : null);
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
    private static string? DescribeMerges(IReadOnlyList<SpeakerFold> merges) =>
        merges.Count == 0
            ? null
            : $"Folded to the speaker count you asked for: merged {string.Join("; ", merges.Select(m => m.Describe()))}. "
              + "A merge with little or no margin may have put two people under one name.";

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
            return "There is audio here but no speech was detected. Try 'fixed windows' on the Settings tab, " +
                   "which decodes everything instead of trusting the detector.";
        }

        return "No speech was found in this file.";
    }

    /// <summary>
    /// The command line's sentence for audible material the gate kept out of a transcript that is
    /// not empty — quiet speech or a fan, which an energy detector cannot tell apart, so it gives
    /// the amount and leaves the judgement to whoever knows what was recorded.
    /// </summary>
    private static string? DescribeUnsegmented(ITranscriptionEngine engine, TranscriptDocument document)
    {
        if (document.IsEmpty
            || (engine as SegmentingTranscriptionEngine)?.LastSegmentationReport is not { UnsegmentedAudibleIsMaterial: true } report)
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{report.UnsegmentedAudibleAudio.TotalSeconds:0.#} s of audio above {report.AudibleThresholdDb:0} dBFS sat below " +
            $"the voice-activity gate and was not decoded. If this is quiet speech over background noise, try " +
            $"'fixed windows' on the Settings tab, which decodes everything.");
    }
}
