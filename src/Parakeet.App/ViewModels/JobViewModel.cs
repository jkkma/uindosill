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

    /// <summary>
    /// Which model and which backend produced this row's speaker labels, or null on a run that did
    /// not label any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A statement of fact, and deliberately not on <see cref="Warning"/>. The warning line used to
    /// carry the diariser backends that did <em>not</em> reproduce the published figure — CUDA and
    /// DirectML — while the two that did produced no line, on the reasoning that a sentence on every
    /// run about a backend that agrees would train people to ignore the line that matters.
    /// <b>Nothing warns about a diariser backend since 2026-08-27</b>: the helper that produced
    /// those sentences went with the engine its measurements belonged to, and no device has been
    /// shown to move this pipeline's labels — which is exactly why the provenance below still
    /// matters, since it is now the only place the device is recorded at all.
    /// </para>
    /// <para>
    /// In the window it left a hole. The Models tab names the backend the <em>transcription</em>
    /// ran on and nothing named the one the speaker labels came from, so a person had no way to
    /// tell a GPU diarisation from a CPU one short of opening the JSON — in a product whose rule is
    /// that a figure is never quoted without its backend. This says it plainly, in the row's
    /// ordinary voice, so the warning line keeps meaning "something needs your attention".
    /// </para>
    /// </remarks>
    [ObservableProperty]
    private string? _speakerProvenance;

    /// <summary>
    /// Which model and which backend produced this row's English, or null on a run that did not
    /// translate.
    /// </summary>
    /// <remarks>
    /// The twin of <see cref="SpeakerProvenance"/>, added the same day and for the same hole:
    /// <c>DescribeTranslator</c> speaks only when the parity check has a finding or when <c>auto</c>
    /// fell back, so an English run on the provider that agrees said nothing at all. Its own
    /// property rather than a clause on the speakers' line, because the two passes are independent
    /// — either can run without the other, and either can fail while the other succeeds — and one
    /// string would have to be rebuilt to say so.
    /// </remarks>
    [ObservableProperty]
    private string? _translationProvenance;

    [ObservableProperty]
    private string _transcript = string.Empty;

    /// <summary>
    /// The English transcript, when the run was a translated one, and empty when it was not.
    /// </summary>
    /// <remarks>
    /// Beside <see cref="Transcript"/> rather than over it, which is the whole reason the window
    /// can offer a switcher at all. The command line keeps both for a narrower purpose — the
    /// anomaly checks read the transcript as the engine wrote it, because a translated segment has
    /// no word confidences — and the window keeps both for this one: a person who asked for
    /// English still wants to see what was actually said, and a pane that replaced the source
    /// would make the two impossible to compare.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranslation))]
    private string _translatedTranscript = string.Empty;

    /// <summary>
    /// Serialises a progress report against completion. On the UI thread the two cannot interleave
    /// — <c>Progress&lt;T&gt;</c> posts through the synchronisation context — but a host without one
    /// delivers the report on a pool thread, and a report that read "not finished" before
    /// <see cref="Complete"/> ran and wrote its status after it left a done row saying
    /// "Transcribing 00:00:03" under a state of Completed. Seen in the test host, 2026-08-22.
    /// </summary>
    private readonly object _gate = new();

    public JobViewModel(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        DisplayName = FileName;
    }

    public string Path { get; }

    public string FileName { get; }

    /// <summary>
    /// The link this row was fetched from, or null for a file somebody dropped in.
    /// </summary>
    /// <remarks>
    /// Kept because the audio on disk is only half of what a link gives you: the Ask tab streams
    /// the picture from here rather than downloading it, so a three-hour video costs a few
    /// megabytes of audio and nothing else. It is also what the row shows instead of a temporary
    /// file name nobody chose.
    /// </remarks>
    public string? SourceUrl { get; init; }

    /// <summary>Whether this row came from a link rather than from a file.</summary>
    public bool IsFromUrl => SourceUrl is not null;

    /// <summary>
    /// What the queue calls this row: the title the site gave it for a link, the file name
    /// otherwise.
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// How long the recording is, read from its header when it was queued, or null when that could
    /// not be answered from the header.
    /// </summary>
    /// <remarks>
    /// A property of the file rather than of a run, which is why <see cref="Reset"/> leaves it
    /// alone: running a file a second time does not change its length. It is what the window's
    /// long-recording warning is computed over, and it has to exist before anything is decoded —
    /// the point of that warning is that it is readable while there is still a decision to make.
    /// </remarks>
    public TimeSpan? Duration { get; init; }

    public List<string> OutputFiles { get; } = [];

    /// <summary>
    /// The transcript as the window draws it: one entry per sentence where a segment's word timings
    /// can tell its sentences apart, otherwise one per segment, each carrying its speaker as a chip
    /// rather than as a prefix on the text.
    /// </summary>
    /// <remarks>
    /// Beside <see cref="Transcript"/> rather than instead of it. That string is what a person
    /// copies out of the window and what the pipeline's own tests pin; this is the view's shape.
    /// The sentence cut is <see cref="TranscriptLineViewModel.LinesFor"/>'s, applied here and on the
    /// pane that fills mid-decode alike; the document's segments are not touched by it.
    /// </remarks>
    public System.Collections.ObjectModel.ObservableCollection<TranscriptLineViewModel> Lines { get; } = [];

    /// <summary>
    /// The English transcript in the same shape, empty on a run that did not translate. Speakers
    /// and their chips are carried across unchanged, because translating a line does not change
    /// whose line it is — an invariant <c>TranscriptTranslation</c> enforces rather than assumes.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<TranscriptLineViewModel> TranslatedLines { get; } = [];

    /// <summary>
    /// The voices in this recording, in the order they are first heard — which is the order their
    /// colours were assigned in, and the order the diariser numbers them in.
    /// </summary>
    /// <remarks>
    /// The same objects the lines of both panes point at, so a name typed here reaches every cue
    /// of that speaker in both panes at once. Empty on a transcript that was never labelled, which
    /// is what the window reads to decide whether to offer any of this at all.
    /// </remarks>
    public System.Collections.ObjectModel.ObservableCollection<SpeakerViewModel> Speakers { get; } = [];

    /// <summary>
    /// The transcript as the engine wrote it, kept so that it can be rendered again later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing retained this until 2026-08-23, and the reason it has to now is the reason a rename
    /// used to reach nothing: <c>TranscriptWriter.WriteAsync</c> runs before <see cref="Complete"/>,
    /// so by the time a reader can name a speaker the only copy of the document had been rendered
    /// to strings and dropped. Adding a transcript to a media file re-renders it at that moment,
    /// with whatever names are on the voices by then, which is what makes the names reach a file at
    /// all.
    /// </para>
    /// <para>
    /// The spoken one rather than the translated one, for the same reason the chip map is built
    /// from the spoken one: the labels are its fact. What is muxed is what was said.
    /// </para>
    /// <para>
    /// It costs memory — a three-hour transcript is its segments and every word's timing, a few
    /// megabytes — and that is the price of the feature rather than an oversight. It is dropped by
    /// <see cref="Reset"/> along with everything else the last run produced.
    /// </para>
    /// </remarks>
    public TranscriptDocument? Document { get; private set; }

    /// <summary>
    /// The English document, kept when the run translated so the Export button can write English
    /// files after the fact; null when it did not. Dropped by <see cref="Reset"/> with the rest.
    /// </summary>
    public TranscriptDocument? TranslatedDocument { get; private set; }

    /// <summary>Whether there is a transcript to put back inside the recording.</summary>
    public bool CanExport => Document is not null;

    /// <summary>
    /// The retained transcript with the reader's names on it, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Built at the moment it is asked for rather than kept alongside, so it cannot go stale
    /// against a name typed a second ago. Where nobody has renamed anything this is the retained
    /// document itself — <c>WithSpeakerNames</c> returns its argument when the map changes nothing.
    /// </remarks>
    public TranscriptDocument? Named() =>
        Document?.WithSpeakerNames(RenamedVoices());

    /// <summary>The English document under the same names, or null when the run did not translate.</summary>
    public TranscriptDocument? NamedTranslation() =>
        TranslatedDocument?.WithSpeakerNames(RenamedVoices());

    /// <summary>
    /// The reader's names for the labelled voices, label → name, renamed rows only. Public since
    /// 2026-08-30 for the Ask panel, which runs its questions over the named document so the
    /// model attributes claims in the reader's own names rather than in labels they renamed away.
    /// </summary>
    public Dictionary<string, string> RenamedVoices() =>
        Speakers
            .Where(voice => voice.IsRenamed)
            .ToDictionary(voice => voice.Label, voice => voice.Name, StringComparer.Ordinal);

    /// <summary>
    /// Whether this row has an English transcript to switch to. What the pane switcher's
    /// visibility hangs on: a run that did not translate gets no switcher rather than a dead
    /// second pill.
    /// </summary>
    public bool HasTranslation => TranslatedTranscript.Length > 0;

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

        lock (_gate)
        {
            ApplyCore(progress);
        }
    }

    private void ApplyCore(TranscriptionProgress progress)
    {
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
        Status = progress.Detail is { Length: > 0 } detail ? detail : progress.Stage switch
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
    /// Puts the row at the start of a second pass: this status, no percentage yet.
    /// </summary>
    /// <remarks>
    /// The transcription pass ends at 100%, and the opt-in passes that follow it reported nothing
    /// until their own first report arrived — which for speaker labelling is after the whole file
    /// has been read and resampled a second time. So the row showed a full bar under "Labelling
    /// speakers" for minutes, which is indistinguishable from a job that has stopped. An
    /// indeterminate bar is the honest state here: work is happening and there is no number for it
    /// yet, as opposed to a number that belongs to work already finished.
    /// </remarks>
    public void BeginPass(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        lock (_gate)
        {
            if (IsFinished)
            {
                return;
            }

            Status = status;
            Progress = 0;
            IsIndeterminate = true;
        }
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
        SpeakerProvenance = null;
        TranslationProvenance = null;
        Transcript = string.Empty;
        TranslatedTranscript = string.Empty;

        // Documents before lines: clearing the line collections is what the ask panel listens
        // to, and its refresh re-reads the documents — cleared afterwards, it kept the discarded
        // transcript live, its citation chips seeking into a run the row no longer showed
        // (found 2026-08-30).
        Document = null;
        TranslatedDocument = null;
        Lines.Clear();
        TranslatedLines.Clear();
        Speakers.Clear();
        OutputFiles.Clear();
        OnPropertyChanged(nameof(CanExport));
    }

    /// <summary>
    /// Puts a finished run on the row. <paramref name="source"/> is the transcript as the engine
    /// wrote it, given only when the run translated — <see cref="JobResult.Document"/> is the
    /// English one by then, and the window shows both.
    /// </summary>
    /// <remarks>
    /// Optional rather than a second overload because the reconciliation pass at the end of a
    /// batch completes failed and cancelled rows through this same method and has no documents at
    /// all. Null means what it says: one transcript, no translation, no switcher.
    /// </remarks>
    public void Complete(JobResult result, TranscriptDocument? source = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            CompleteCore(result, source);
        }
    }

    private void CompleteCore(JobResult result, TranscriptDocument? source)
    {
        State = result.State;
        IsIndeterminate = false;
        Progress = 100;
        Error = result.Error;
        OutputFiles.Clear();
        OutputFiles.AddRange(result.OutputFiles);

        // A pass that failed is said twice on purpose, and in two registers: the status names what
        // the row is missing — "Done — 2 files, without speaker labels" — so it reads at a glance
        // beside the rows that have them, and the warning line carries the reason. "Done" alone
        // over a transcript that was asked for speakers and has none would be this window's own
        // silent failure.
        var without = result.FailedPasses.Count == 0
            ? string.Empty
            : ", without " + string.Join(" or ", result.FailedPasses.Select(f => f.Pass.Product).Distinct());

        Warning = result.FailedPasses.Count == 0
            ? result.Warning
            : string.Join(" ", result.FailedPasses.Select(f => f.Describe()).Append(result.Warning).Where(w => w is { Length: > 0 }));

        Status = result.State switch
        {
            JobState.Completed when result.OutputFiles.Count > 0 =>
                $"Done: {result.OutputFiles.Count} file{(result.OutputFiles.Count == 1 ? string.Empty : "s")}{without}",
            JobState.Completed => "Done" + without,
            JobState.Failed => "Failed",
            JobState.Cancelled => "Cancelled",
            _ => "Done",
        };

        if (result.Document is { } document)
        {
            // With a source in hand the result's document is the English one, and the two panes
            // are filled from different documents. Without one there is nothing to switch to and
            // the translated half stays empty, which is what HasTranslation reads.
            var spoken = source ?? document;

            // One chip map, built from the spoken document and used for both panes. Built per pane
            // over each pane's non-empty segments, as it was until 2026-08-22, the two could
            // disagree: a speaker whose first segment came back empty from the translator appeared
            // later in the English pane's walk and took a different chip there.
            var chips = Voices(spoken);

            // Rebuilt rather than merged. A second run over the same audio need not give "Speaker 1"
            // to the same person, so carrying a name across would be this window asserting an
            // identity it cannot check.
            Speakers.Clear();
            foreach (var voice in chips.Values)
            {
                Speakers.Add(voice);
            }

            // Read off the spoken document rather than the result's. Both carry it — translation
            // derives from the labelled document and may not change who said a segment — but the
            // labels are the spoken one's fact, and reading provenance from the document that
            // owns it is what keeps that true if the translation contract is ever widened.
            SpeakerProvenance = spoken.SpeakerBackend is { } speakerBackend
                ? $"Speakers: {spoken.SpeakerModelId ?? "unnamed model"} on {speakerBackend.ToString().ToLowerInvariant()}"
                : null;

            // The result's document rather than the spoken one, which is the mirror image of the
            // line above and for the same reason: TranscriptTranslation stamps these onto the
            // English document it returns, so that is the document that owns the fact.
            TranslationProvenance = document.TranslationBackend is { } translationBackend
                ? $"English: {document.TranslationModelId ?? "unnamed model"} on {translationBackend.ToString().ToLowerInvariant()}"
                : null;

            Document = spoken;
            TranslatedDocument = source is not null ? document : null;
            OnPropertyChanged(nameof(CanExport));

            Transcript = Render(spoken);
            Relines(spoken, chips, Lines);

            if (source is not null)
            {
                TranslatedTranscript = Render(document);
                Relines(document, chips, TranslatedLines);
            }
        }
    }

    /// <summary>
    /// The transcript as the window shows it: <see cref="TranscriptDocument.Text"/> — the joined
    /// segments — with the speaker in front of each segment that has one. The document's own
    /// <c>Text</c> stays free of names on purpose: it is what the JSON's <c>text</c> field carries
    /// and what a word error rate is scored on, and a name is not a word anybody said.
    /// </summary>
    /// <summary>
    /// Rebuilds one of the line collections from a finished document, assigning each speaker one
    /// of the eight chip styles in the order they are first heard.
    /// </summary>
    /// <remarks>
    /// The diariser numbers speakers in the order it first hears them, and this follows that same
    /// order rather than sorting by name — so the chip a speaker gets does not move when a name is
    /// edited, and two files transcribed in the same session do not swap colours between them.
    ///
    /// It is also what keeps the two panes' chips in step. Each document is walked separately and
    /// builds its own map, and they agree because translation is forbidden from changing who said
    /// a segment or how many there are: same speakers, same order, same colours either side of the
    /// switcher. A speaker whose every segment came back empty is the one case the two can differ,
    /// and an empty line is dropped from both.
    ///
    /// The modulo is a backstop rather than a policy. The shipping diariser has no speaker cap, so
    /// the ninth speaker wraps to the first chip rather than throwing — a colour clash is a smaller
    /// failure than a window that will not draw a transcript — and it wraps at the same eight as
    /// <see cref="Voices"/>, so a backstop chip agrees with the palette the map was built from.
    /// </remarks>
    private static void Relines(
        TranscriptDocument document,
        Dictionary<string, SpeakerViewModel> chips,
        System.Collections.ObjectModel.ObservableCollection<TranscriptLineViewModel> target)
    {
        target.Clear();

        foreach (var segment in document.Segments.Where(s => !s.IsEmpty))
        {
            SpeakerViewModel? voice = null;

            if (segment.Speaker is { } speaker && !chips.TryGetValue(speaker, out voice))
            {
                // Not in the spoken document at all — which the translation contract forbids, since
                // a translator may not change who said a segment — so a backstop rather than a path:
                // the next chip, recorded so the next line of the same speaker agrees with this one.
                voice = new SpeakerViewModel(speaker, chips.Count % 8);
                chips[speaker] = voice;
            }

            // The words come across too, and they are what the Ask tab marks the spoken one from
            // and cuts the segment into sentences by. A translated document carries none —
            // `SidecarTranscriptTranslator` writes an empty list, because translating loses the
            // timing of individual words — so the English pane gets one line per segment with
            // segment times and no word times, which is exactly what it is entitled to.
            foreach (var line in TranscriptLineViewModel.LinesFor(segment, voice))
            {
                target.Add(line);
            }
        }
    }

    /// <summary>
    /// Each speaker's chip, in the order they are first heard in <paramref name="document"/> —
    /// over every segment, empty ones included, so that the map is a property of the recording
    /// and not of which segments happen to carry text.
    /// </summary>
    internal static Dictionary<string, SpeakerViewModel> Voices(TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var voices = new Dictionary<string, SpeakerViewModel>(StringComparer.Ordinal);
        foreach (var speaker in document.Segments.Select(s => s.Speaker).OfType<string>())
        {
            if (!voices.ContainsKey(speaker))
            {
                // **Eight since 2026-08-27, and it is still a wrap.** It was `% 4` because four
                // was the ONNX diariser's architectural ceiling — a fifth speaker could not occur,
                // so the modulo was unreachable rather than lossy. That engine is in
                // `attic/sortformer/` and the pipeline that replaced it clusters with no cap, so
                // five and more became ordinary; AMI-style meetings have five to seven. The
                // palette grew to match (Theme/Controls.axaml), which moves the collision to the
                // ninth speaker rather than removing it — and nine in one recording would repeat a
                // colour while still reading a different name, which is the same graceful failure
                // as before, further out.
                voices[speaker] = new SpeakerViewModel(speaker, voices.Count % 8);
            }
        }

        return voices;
    }

    internal static string Render(TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return string.Join(" ", document.Segments
            .Where(s => !s.IsEmpty)
            .Select(s => s.Speaker is { } speaker ? $"{speaker}: {s.Text.Trim()}" : s.Text.Trim()));
    }
}
