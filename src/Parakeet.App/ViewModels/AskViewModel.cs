using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parakeet.App.Services;

namespace Parakeet.App.ViewModels;

/// <summary>
/// The Ask tab: a recording you can play, its transcript beside it as cues you can click, and the
/// chat panel that is not built.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is here and what is not.</b> <c>docs/V2-ASK-THE-TRANSCRIPT.md</c> argues that playback
/// should land before any language model does — "a transcript you can click to hear is useful
/// before any model is involved" — and that is exactly the half this is. The transport and the
/// cues are real and work on v1's own data: word and segment timings the pipeline already writes.
/// The chat panel is drawn, disabled, and covered by a notice saying so, because there is no local
/// model behind it and every decision about which one is still open in that document.
/// </para>
/// <para>
/// <b>The window never writes a timestamp of its own.</b> Every time drawn here comes off a
/// <c>TranscriptSegment</c> unchanged. That is the same rule the citation design sets for the model
/// — cite by segment, resolve to a time in the application — arriving early, in the place where it
/// is cheapest to keep.
/// </para>
/// <para>
/// <b>The queue is shared with the Transcribe tab rather than copied.</b> The same
/// <c>JobViewModel</c> objects appear on both, so a file whose transcript finishes while this tab
/// is open fills in where it stands, and a file dropped here-and-now is playable before it has been
/// transcribed at all. Two lists would need reconciling, and the failure mode of getting that wrong
/// is a transcript shown beside the wrong recording.
/// </para>
/// <para>
/// There is no clock in here. <see cref="Tick"/> is called by the window, which owns the timer;
/// this reads the player's position when it is asked to. That is what lets the whole tab be driven
/// in a test by advancing a fake player by hand.
/// </para>
/// </remarks>
public sealed partial class AskViewModel : ObservableObject, IDisposable
{
    private readonly IMediaPlayer _player;

    /// <summary>What the last <see cref="Tick"/> drew, so an unchanged one costs nothing.</summary>
    private TimeSpan _drawnPosition = TimeSpan.MinValue;
    private bool _drawnIsPlaying;
    private bool _drawnHasVideo;

    private TranscriptLineViewModel? _activeLine;
    private bool _disposed;

    /// <summary>The lines carrying the search term, in the order they are spoken.</summary>
    private readonly List<TranscriptLineViewModel> _matches = [];

    /// <summary>Which of <see cref="_matches"/> the search is standing on, or -1 for none.</summary>
    private int _matchIndex = -1;

    [ObservableProperty]
    private JobViewModel? _selectedRecording;

    /// <summary>
    /// What to look for in the transcript. Every line carrying it is marked as it is typed; Enter
    /// steps through them.
    /// </summary>
    [ObservableProperty]
    private string? _searchTerm;

    /// <summary>
    /// Why this recording cannot be played, or null when it can. A reason rather than a dead
    /// button: a transport that does nothing and says nothing is this window's own silent failure.
    /// </summary>
    [ObservableProperty]
    private string? _playbackNotice;

    public AskViewModel(ObservableCollection<JobViewModel> recordings, IMediaPlayer player)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        ArgumentNullException.ThrowIfNull(player);

        Recordings = recordings;
        _player = player;

        Recordings.CollectionChanged += OnRecordingsChanged;
    }

    /// <summary>
    /// The queue, as the Transcribe tab holds it. Everything in it, not only what has been
    /// transcribed: a recording is playable the moment it is dropped, and its transcript appears
    /// underneath when the run finishes.
    /// </summary>
    public ObservableCollection<JobViewModel> Recordings { get; }

    public bool HasRecordings => Recordings.Count > 0;

    /// <summary>The transcript of the selected recording, or null when none is selected.</summary>
    /// <summary>
    /// The lines the tab is showing: the transcript as it was said, or its English translation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything on this tab reads the transcript through here — the highlight that follows the
    /// playhead, the find box, the cue a click seeks from — so switching the pane switches all of
    /// them at once and none of them needed to know about translation.
    /// </para>
    /// <para>
    /// <b>The English pane marks no words, and that is the translator's doing rather than an
    /// omission here.</b> A translated segment carries its start and end but no word times:
    /// <c>SidecarTranscriptTranslator</c> writes an empty word list because translating loses which
    /// word was said when, and a word's position in an English sentence is not a fact about the
    /// Spanish audio. So the English cues seek and highlight by line, and the word mark simply has
    /// nothing to find — which is why the switcher exists rather than the English replacing the
    /// transcript.
    /// </para>
    /// <para>
    /// Falls back to the spoken lines for a recording with no translation, so an index left over
    /// from a row that had one cannot strand anybody on an empty pane.
    /// </para>
    /// </remarks>
    public ObservableCollection<TranscriptLineViewModel>? Lines =>
        SelectedRecording is not { } job ? null
        : TranscriptPane == 1 && job.HasTranslation ? job.TranslatedLines
        : job.Lines;

    /// <summary>Whether there is an English version of the open recording to switch to.</summary>
    public bool CanShowTranslation => SelectedRecording?.HasTranslation ?? false;

    /// <summary>Whether the pane being read is the English one.</summary>
    public bool IsShowingTranslation => TranscriptPane == 1 && CanShowTranslation;

    /// <summary>
    /// Why no word lights up while the English is being read, and why its lines are whole segments
    /// where the transcript's are sentences — or null on the spoken transcript.
    /// </summary>
    /// <remarks>
    /// Said rather than left to be noticed: the mark and the sentence cut both work on one pane and
    /// not the other, and a feature that silently stops is indistinguishable from a broken one. Both
    /// have the same cause — a translated segment carries no word times — so it is one sentence.
    /// </remarks>
    public string? TranslationPaneNotice =>
        IsShowingTranslation
            ? "The English follows the recording a segment at a time. Individual words are not marked here "
              + "and a segment's sentences are not split apart — translating loses which word was said when — "
              + "so switch to Transcript for either."
            : null;

    public bool HasTranscript => Lines is { Count: > 0 };

    /// <summary>Which pane is being read: 0 the transcript, 1 its English version.</summary>
    /// <remarks>
    /// On this tab rather than on the row, for the reason the Transcribe tab gives for the same
    /// choice: moving down the queue keeps the pane a person picked instead of snapping back to the
    /// source on every selection.
    /// </remarks>
    [ObservableProperty]
    private int _transcriptPane;

    partial void OnTranscriptPaneChanged(int value)
    {
        // The whole tab reads the transcript through Lines, so everything derived from it moves.
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(HasTranscript));
        OnPropertyChanged(nameof(TranscriptNotice));
        OnPropertyChanged(nameof(IsShowingTranslation));
        OnPropertyChanged(nameof(TranslationPaneNotice));

        // The line being played is a line of the pane that was showing. Dropped and found again by
        // position rather than by index: the two panes share the segment times, so the same moment
        // is the same place in both, and an index would be right only while neither pane had
        // dropped an empty segment.
        SetActiveLine(null);
        Redraw();

        // And the hits, which are matches in the text of one pane and mean nothing in the other's.
        Research();
    }

    /// <summary>The voices in the open recording, or null where it was never labelled.</summary>
    /// <remarks>
    /// The recording's own objects rather than copies, which is what lets a name typed on the strip
    /// reach every cue of that speaker — and the Transcribe tab's rows for the same file — without
    /// this class forwarding anything.
    /// </remarks>
    public System.Collections.ObjectModel.ObservableCollection<SpeakerViewModel>? Speakers =>
        SelectedRecording?.Speakers;

    /// <summary>Whether there is anything to rename.</summary>
    public bool HasSpeakers => Speakers is { Count: > 0 };

    /// <summary>
    /// What a name typed on this strip does and does not do, said once somebody has typed one.
    /// </summary>
    /// <remarks>
    /// Null until a name actually differs from its label. A standing caveat over a feature nobody
    /// has used yet is noise, and a reader who has just renamed a speaker is the one person for
    /// whom it is not: they are about to close the window and expect to find the name again.
    /// </remarks>
    public string? RenameNotice =>
        Speakers is { } voices && voices.Any(v => v.IsRenamed)
            ? "Names are for reading here. The transcript files already written keep the diariser's "
              + "own labels, and a new run starts over."
            : null;

    /// <summary>
    /// What stands in for the transcript when there is not one, and null when there is. Two
    /// different nothings, said differently: no recording chosen, and one chosen that has not been
    /// transcribed.
    /// </summary>
    public string? TranscriptNotice =>
        HasTranscript ? null
        : SelectedRecording is null ? "Choose a recording on the left. Anything in the queue on the Transcribe tab is here."
        : "This recording has not been transcribed yet. Run it on the Transcribe tab and its words will appear here, "
          + "each one a place in the recording you can click. It plays either way.";

    /// <summary>The line the recording is inside right now, or null.</summary>
    public TranscriptLineViewModel? ActiveLine => _activeLine;

    /// <summary>
    /// Where that line sits in <see cref="Lines"/>, or -1. What the window follows the playhead
    /// by: an index, because bringing a row into view is something only the control that has
    /// realised it can do — the same division the search already uses for its hits.
    /// </summary>
    public int ActiveLineIndex =>
        _activeLine is { } line && Lines is { } lines ? lines.IndexOf(line) : -1;

    // ── Finding a word ────────────────────────────────────────────────────────────────────────
    //
    // A transcript is a wall of text and the reason anyone opens a three-hour one is usually a
    // single word. Searching it is v1's own data and needs no model, so it belongs here for the
    // same reason the transport does: it is useful before anything can be asked.
    //
    // What it does not do is seek. Finding a word and hearing it are two intentions — a reader
    // scanning for every mention of a name does not want the audio jumping under them on each
    // press of Enter. The hit is scrolled to and marked; clicking it is what plays it.

    /// <summary>How many lines carry the term.</summary>
    public int MatchCount => _matches.Count;

    /// <summary>Whether there is anything to step between.</summary>
    public bool CanStepMatches => _matches.Count > 0;

    /// <summary>The hit the search is standing on, or null.</summary>
    public TranscriptLineViewModel? CurrentMatch =>
        _matchIndex >= 0 && _matchIndex < _matches.Count ? _matches[_matchIndex] : null;

    /// <summary>
    /// Where that hit sits in <see cref="Lines"/>, or -1. What the window scrolls to: an index,
    /// because bringing a row into view is something only the control that realises it can do.
    /// </summary>
    public int CurrentMatchLineIndex =>
        CurrentMatch is { } match && Lines is { } lines ? lines.IndexOf(match) : -1;

    /// <summary>
    /// "3 of 17", or that there are none, or nothing at all while the box is empty. An empty box
    /// is not a search that found nothing, and saying "No matches" over one would be this window
    /// answering a question nobody asked.
    /// </summary>
    public string? SearchSummary =>
        string.IsNullOrWhiteSpace(SearchTerm) ? null
        : _matches.Count == 0 ? "No matches"
        : string.Create(CultureInfo.InvariantCulture, $"{_matchIndex + 1} of {_matches.Count}");

    /// <summary>Steps to the next hit, wrapping round at the end.</summary>
    /// <remarks>
    /// Wrapping rather than stopping. A find bar that goes dead at the last hit makes the reader
    /// work out where they are in a transcript they are searching precisely because they do not
    /// know; the counter beside it says when it has come round.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanStepMatches))]
    private void NextMatch()
    {
        if (_matches.Count == 0)
        {
            return;
        }

        _matchIndex = (_matchIndex + 1) % _matches.Count;
        NotifySearch();
    }

    [RelayCommand(CanExecute = nameof(CanStepMatches))]
    private void PreviousMatch()
    {
        if (_matches.Count == 0)
        {
            return;
        }

        _matchIndex = (_matchIndex - 1 + _matches.Count) % _matches.Count;
        NotifySearch();
    }

    partial void OnSearchTermChanged(string? value) => Research();

    /// <summary>
    /// Re-marks the transcript for the current term and lands the search on the first hit.
    /// </summary>
    /// <remarks>
    /// The term is written only onto the lines that carry it, and taken off the ones that used to.
    /// A search that set it on every line would rebuild every paragraph on every keystroke —
    /// fifteen hundred of them on a three-hour recording, all but a handful re-rendering to
    /// exactly what they already said.
    /// </remarks>
    private void Research()
    {
        foreach (var line in _matches)
        {
            line.SearchTerm = null;
            line.IsCurrentMatch = false;
        }

        _matches.Clear();

        var term = SearchTerm?.Trim();

        if (!string.IsNullOrEmpty(term) && Lines is { } lines)
        {
            foreach (var line in lines)
            {
                if (line.Mentions(term))
                {
                    line.SearchTerm = term;
                    _matches.Add(line);
                }
            }
        }

        // Back to the top on every change to the term, which is what a find bar does: typing
        // another letter is a new search, not a continuation of the old one's position.
        _matchIndex = _matches.Count == 0 ? -1 : 0;
        NotifySearch();
    }

    private void NotifySearch()
    {
        var current = CurrentMatch;

        foreach (var line in _matches)
        {
            line.IsCurrentMatch = ReferenceEquals(line, current);
        }

        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(CanStepMatches));
        OnPropertyChanged(nameof(CurrentMatch));
        OnPropertyChanged(nameof(SearchSummary));

        // Last, and it matters: the window scrolls off this one, and it has to be raised after the
        // marking above so that what is brought into view is already drawn as the current hit.
        OnPropertyChanged(nameof(CurrentMatchLineIndex));

        NextMatchCommand.NotifyCanExecuteChanged();
        PreviousMatchCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Whether there is something open that can be played.</summary>
    public bool CanPlay => _player.Path is not null;

    /// <summary>
    /// The player itself, for the one thing a binding cannot carry: the window's video surface
    /// subscribes to <see cref="IMediaPlayer.FrameReady"/> and copies frames straight out of it,
    /// because a bitmap redrawn thirty times a second has no business round-tripping through
    /// property notifications. Everything else on this tab still goes through the properties.
    /// </summary>
    public IMediaPlayer Player => _player;

    /// <summary>Whether the open recording has a picture the build is drawing.</summary>
    public bool HasVideo => _player.HasVideo;

    /// <summary>
    /// Why there is no picture when one might be expected, or null. Only for a build without a
    /// video player and a file whose container usually carries one: an audio file gets no notice,
    /// because the absence of a picture is not a limitation there.
    /// </summary>
    public string? VideoNotice =>
        _player.CanDrawVideo || SelectedRecording is not { } job || !(job.IsFromUrl || LooksLikeVideo(job.Path))
            ? null
            : "This build has no video player, so if this recording has a picture, only its sound "
              + "plays. Vendoring libmpv adds the picture — see docs/NATIVE-BINARIES.md.";

    private static readonly string[] VideoExtensions = [".mp4", ".m4v", ".mov", ".wmv", ".asf", ".mkv", ".webm", ".avi"];

    private static bool LooksLikeVideo(string path) =>
        VideoExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public bool IsPlaying => _player.IsPlaying;

    public double PositionSeconds => _player.Position.TotalSeconds;

    /// <summary>
    /// The bar's maximum. Never zero: a <c>ProgressBar</c> whose maximum equals its minimum draws
    /// itself full, so an unopened recording would show a completed one.
    /// </summary>
    public double DurationSeconds => Math.Max(_player.Duration.TotalSeconds, 1);

    public string PositionLabel => Timecode.Format(_player.Position);

    public string DurationLabel => Timecode.Format(_player.Duration);

    /// <summary>What the transport's button does next, for its tooltip and for a screen reader.</summary>
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    /// <summary>
    /// The four questions the panel would open with. Drawn and disabled, because a suggestion is a
    /// promise that pressing it does something.
    /// </summary>
    /// <remarks>
    /// Kept here rather than in the view because they are the feature's own content and belong
    /// beside the notice that explains why they do not work yet — and because when the panel is
    /// built these become a real command's parameters rather than four literals in a layout.
    /// </remarks>
    public IReadOnlyList<string> Suggestions { get; } =
    [
        "What are the main topics?",
        "Summarise the first ten minutes",
        "What was said about pricing?",
        "Find where they disagree",
    ];

    public string WorkInProgressTitle => "Work in progress";

    /// <summary>
    /// The notice over the chat panel. It says which half of this tab is real, because the other
    /// half is drawn beneath it and a covered control that does not explain itself reads as broken
    /// rather than as unbuilt.
    /// </summary>
    public string WorkInProgressNotice =>
        "Asking questions is not built. There is no language model in this application yet, and what one would be "
        + "is still an open decision — see docs/V2-ASK-THE-TRANSCRIPT.md.\n\n"
        + "The recording and its transcript beside it are real: play it, and click any line to jump there.";

    /// <summary>
    /// Plays, or pauses. From the end of a recording it starts again, which is what a play button
    /// at the end of a recording is expected to do.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void PlayPause()
    {
        if (_player.IsPlaying)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }

        Redraw();
    }

    /// <summary>Jumps to a line of the transcript and plays from there.</summary>
    /// <remarks>
    /// It plays rather than merely seeking. Clicking a line is a request to hear it, and a seek
    /// that leaves the transport paused makes the reader press two things for one intention.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void SeekToLine(TranscriptLineViewModel? line)
    {
        if (line is null)
        {
            return;
        }

        _player.Seek(line.Start);
        _player.Play();
        Redraw();
    }

    /// <summary>
    /// Jumps to a fraction of the way through, which is what a press on the seek bar means. The
    /// bar reports where it was pressed and this turns that into a time, because a control that
    /// knows its own width is the only thing that can.
    /// </summary>
    public void SeekToFraction(double fraction)
    {
        if (!CanPlay || double.IsNaN(fraction))
        {
            return;
        }

        var clamped = Math.Clamp(fraction, 0, 1);
        _player.Seek(_player.Duration * clamped);
        Redraw();
    }

    /// <summary>
    /// Reads the player's clock and redraws whatever moved. Called by the window on a timer while
    /// this tab is showing; a call that finds nothing changed does no work and raises nothing.
    /// </summary>
    public void Tick()
    {
        if (_player.Position == _drawnPosition
            && _player.IsPlaying == _drawnIsPlaying
            && _player.HasVideo == _drawnHasVideo)
        {
            return;
        }

        Redraw();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Recordings.CollectionChanged -= OnRecordingsChanged;

        if (SelectedRecording is { } job)
        {
            job.Lines.CollectionChanged -= OnLinesChanged;
            job.TranslatedLines.CollectionChanged -= OnLinesChanged;
            job.PropertyChanged -= OnRecordingChanged;

            // And the voices, which is the subscription that would actually outlive this: a
            // JobViewModel belongs to the Transcribe tab and goes on existing after this one is
            // disposed, so a handler left on one of its speakers is a disposed tab still being
            // told about renames.
            job.Speakers.CollectionChanged -= OnSpeakersChanged;
            Unwatch(job.Speakers);
        }

        _player.Dispose();
    }

    /// <summary>
    /// Opens the newly selected recording and moves the transcript subscription onto it.
    /// </summary>
    /// <remarks>
    /// The subscription is why this is not simply a property: the lines of a recording that is
    /// still being transcribed arrive after it was selected, and without following them the pane
    /// would keep saying "not transcribed yet" over a finished transcript until the selection was
    /// clicked away and back.
    /// </remarks>
    partial void OnSelectedRecordingChanged(JobViewModel? oldValue, JobViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.Lines.CollectionChanged -= OnLinesChanged;
            oldValue.TranslatedLines.CollectionChanged -= OnLinesChanged;
            oldValue.PropertyChanged -= OnRecordingChanged;
            oldValue.Speakers.CollectionChanged -= OnSpeakersChanged;
            Unwatch(oldValue.Speakers);
        }

        if (newValue is not null)
        {
            newValue.Lines.CollectionChanged += OnLinesChanged;

            // Both collections, because the English arrives long after the transcript did and on a
            // row this tab may already be showing.
            newValue.TranslatedLines.CollectionChanged += OnLinesChanged;
            newValue.PropertyChanged += OnRecordingChanged;
            newValue.Speakers.CollectionChanged += OnSpeakersChanged;
            Watch(newValue.Speakers);
        }

        PlaybackNotice = null;

        try
        {
            if (newValue is { } job)
            {
                // A row fetched from a link opens the link rather than the audio beside it, so the
                // picture streams and the sound comes from the same place it was transcribed from.
                // Only where the build can draw a picture: on an audio-only build the link would
                // buy nothing and cost a network round trip on every selection.
                var source = job.IsFromUrl && _player.CanDrawVideo ? job.SourceUrl! : job.Path;
                _player.Open(source);
            }
            else
            {
                _player.Close();
            }
        }
        catch (PlaybackException ex)
        {
            // The transcript is still worth showing; only the transport is lost. So the reason goes
            // where the transport is and the rest of the tab carries on.
            _player.Close();
            PlaybackNotice = ex.Message;
        }

        SetActiveLine(null);
        Redraw();
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(HasTranscript));
        OnPropertyChanged(nameof(TranscriptNotice));
        OnPropertyChanged(nameof(VideoNotice));
        OnPropertyChanged(nameof(CanShowTranslation));
        OnPropertyChanged(nameof(IsShowingTranslation));
        OnPropertyChanged(nameof(TranslationPaneNotice));
        OnPropertyChanged(nameof(Speakers));
        OnPropertyChanged(nameof(HasSpeakers));
        OnPropertyChanged(nameof(RenameNotice));

        // The term survives the change of recording and is run against the new transcript. Somebody
        // looking for a name across a session's worth of files is doing exactly that, and clearing
        // the box for them would mean typing it again for every file.
        Research();
    }

    /// <summary>
    /// The open recording changed something about itself — which, for this tab, means a translation
    /// arrived for a row somebody is already reading.
    /// </summary>
    /// <remarks>
    /// The pane snaps to the English the first time one appears, because asking for a translation
    /// and then having to find where it went is the wrong way round. It snaps once: a reader who
    /// switches back to the transcript stays there, and this only fires again when another
    /// recording gains a translation of its own.
    /// </remarks>
    private void OnRecordingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JobViewModel.HasTranslation)
            && e.PropertyName != nameof(JobViewModel.TranslatedTranscript))
        {
            return;
        }

        OnPropertyChanged(nameof(CanShowTranslation));

        if (CanShowTranslation && TranscriptPane == 0)
        {
            // Assigning raises OnTranscriptPaneChanged, which moves the lines, the highlight and
            // the search with it.
            TranscriptPane = 1;
        }
        else
        {
            OnPropertyChanged(nameof(Lines));
            OnPropertyChanged(nameof(HasTranscript));
            OnPropertyChanged(nameof(IsShowingTranslation));
            OnPropertyChanged(nameof(TranslationPaneNotice));
        }
    }

    /// <summary>
    /// The strip is filled by the labelling pass, which finishes long after the recording was
    /// selected — so the voices arrive on a collection this tab is already showing.
    /// </summary>
    private void OnSpeakersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is System.Collections.ObjectModel.ObservableCollection<SpeakerViewModel> voices)
        {
            Unwatch(e.OldItems?.OfType<SpeakerViewModel>());
            Watch(e.NewItems?.OfType<SpeakerViewModel>());

            // Clear() reports neither, so the whole collection is re-read rather than trusted to
            // have told us what left it.
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                Watch(voices);
            }
        }

        OnPropertyChanged(nameof(HasSpeakers));
        OnPropertyChanged(nameof(RenameNotice));
    }

    /// <summary>
    /// Follows each voice, so that a rename raises the one thing on this class that depends on it.
    /// </summary>
    /// <remarks>
    /// Four subscriptions at most — four is the diariser's architectural ceiling — which is why
    /// this is a subscription per voice rather than anything cleverer. The chips themselves need
    /// none of it: they bind through <c>Voice.Name</c> and hear the voice directly.
    /// </remarks>
    private void Watch(IEnumerable<SpeakerViewModel>? voices)
    {
        foreach (var voice in voices ?? [])
        {
            voice.PropertyChanged -= OnVoiceChanged;
            voice.PropertyChanged += OnVoiceChanged;
        }
    }

    private void Unwatch(IEnumerable<SpeakerViewModel>? voices)
    {
        foreach (var voice in voices ?? [])
        {
            voice.PropertyChanged -= OnVoiceChanged;
        }
    }

    private void OnVoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpeakerViewModel.IsRenamed))
        {
            OnPropertyChanged(nameof(RenameNotice));
        }
    }

    private void OnRecordingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasRecordings));

        // The first recording to arrive is selected, so the tab is not an empty list beside an
        // empty pane the first time it is opened. Only the first: a selection somebody made is
        // never taken off them by a file arriving in the queue behind it.
        if (SelectedRecording is null && Recordings.Count > 0)
        {
            SelectedRecording = Recordings[0];
        }
        else if (SelectedRecording is { } selected && !Recordings.Contains(selected))
        {
            // Cleared out from under this tab — by Clear on the Transcribe tab, which is the only
            // way a row leaves. Dropping the selection closes the file rather than leaving a
            // transport pointing at a row nobody can see.
            SelectedRecording = null;
        }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasTranscript));
        OnPropertyChanged(nameof(TranscriptNotice));

        // A transcript that has just been replaced — by a re-run, or cleared by a reset — leaves
        // the old highlight pointing at a line that is no longer in the collection, and a search
        // standing on hits that have been thrown away.
        SetActiveLine(null);
        UpdateActiveLine(_player.Position);
        Research();
    }

    /// <summary>Re-reads everything the transport draws from the player.</summary>
    private void Redraw()
    {
        _drawnPosition = _player.Position;
        _drawnIsPlaying = _player.IsPlaying;
        _drawnHasVideo = _player.HasVideo;

        UpdateActiveLine(_drawnPosition);

        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayPauseLabel));
        OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(DurationSeconds));
        OnPropertyChanged(nameof(PositionLabel));
        OnPropertyChanged(nameof(DurationLabel));

        PlayPauseCommand.NotifyCanExecuteChanged();
        SeekToLineCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Moves the highlight to whichever line holds <paramref name="position"/>, and the word mark
    /// to whichever of that line's words is being said.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two lines at most, however long the transcript is: the line that had it and the line that
    /// takes it. Setting a flag on every line each tick would be fifteen hundred property
    /// notifications ten times a second on a three-hour recording, all but two of them saying
    /// nothing changed.
    /// </para>
    /// <para>
    /// The word is searched for on the active line alone, and only once the line is settled. It
    /// moves several times a second while the line sits still, which is the whole point of it —
    /// and it costs one walk over one sentence's words per tick, rather than over the transcript.
    /// </para>
    /// </remarks>
    private void UpdateActiveLine(TimeSpan position)
    {
        TranscriptLineViewModel? next = null;

        if (Lines is { } lines)
        {
            foreach (var line in lines)
            {
                if (line.Contains(position))
                {
                    next = line;
                    break;
                }
            }
        }

        SetActiveLine(next);

        if (_activeLine is { } active)
        {
            // Assigned rather than compared first: the property is observable and does not raise
            // when the value is unchanged, so a tick that lands inside the same word costs a
            // comparison and nothing else.
            active.SpokenWord = active.WordAt(position);
        }
    }

    private void SetActiveLine(TranscriptLineViewModel? line)
    {
        if (ReferenceEquals(line, _activeLine))
        {
            return;
        }

        if (_activeLine is not null)
        {
            _activeLine.IsActive = false;

            // A line that is no longer being played has no word being said in it. Left set, the
            // mark would stay behind on the line the playhead has left — one word lit in a
            // paragraph nobody is inside, which reads as the highlight having got stuck.
            _activeLine.SpokenWord = -1;
        }

        _activeLine = line;

        if (_activeLine is not null)
        {
            _activeLine.IsActive = true;
        }

        OnPropertyChanged(nameof(ActiveLine));
        OnPropertyChanged(nameof(ActiveLineIndex));
    }
}
