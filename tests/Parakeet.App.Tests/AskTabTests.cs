using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

/// <summary>
/// The Ask tab: the transport, the transcript as cues that seek it, and the chat panel that is
/// not built.
/// </summary>
/// <remarks>
/// Everything here runs against <see cref="FakeMediaPlayer"/>, whose clock moves only when it is
/// told to. That is not a convenience — WASAPI needs a Windows output endpoint and neither CI nor
/// a headless run has one — and it is also what makes these deterministic: a test that waited for
/// a real device to play ten seconds of audio would take ten seconds and still be a race.
/// <see cref="SystemAudioPlayer"/> itself is exercised by running the application; see
/// <c>docs/UNPROVEN.md</c>.
/// </remarks>
public class AskTabTests
{
    [Fact]
    public void TheFirstRecordingIsOpenedAndALaterOneDoesNotStealTheSelection()
    {
        var (ask, jobs, player) = Create();

        Assert.False(ask.HasRecordings);
        Assert.Null(ask.SelectedRecording);
        Assert.False(ask.CanPlay);

        jobs.Add(Transcribed("/tmp/a.wav"));

        // The tab opens on something rather than on an empty list beside an empty pane.
        Assert.True(ask.HasRecordings);
        Assert.Same(jobs[0], ask.SelectedRecording);
        Assert.Equal("/tmp/a.wav", player.Path);
        Assert.True(ask.CanPlay);

        // Only the first. A file arriving in the queue behind a selection somebody made does not
        // take it off them, and does not re-open the device under a recording that is playing.
        jobs.Add(Transcribed("/tmp/b.wav"));
        Assert.Same(jobs[0], ask.SelectedRecording);
        Assert.Equal(1, player.Opens);
    }

    [Fact]
    public void ClearingTheQueueClosesWhateverThisTabHadOpen()
    {
        var (ask, jobs, player) = Create();
        jobs.Add(Transcribed());

        jobs.Clear();

        Assert.Null(ask.SelectedRecording);
        Assert.Null(player.Path);
        Assert.False(ask.CanPlay);
        Assert.Null(ask.Lines);
        Assert.False(ask.HasTranscript);
    }

    [Fact]
    public void ARecordingThisBuildCannotSoundKeepsItsTranscriptAndSaysWhyTheTransportIsDead()
    {
        // The two halves of this tab fail independently. A container with no decoder on this
        // machine is still a transcript worth reading, so the reason goes where the transport is
        // and the words stay where they are.
        var (ask, jobs, player) = Create();
        player.RefuseWith = "Could not play 'a.wav'. This machine has no decoder for it.";

        jobs.Add(Transcribed());

        Assert.False(ask.CanPlay);
        Assert.Equal(player.RefuseWith, ask.PlaybackNotice);
        Assert.True(ask.HasTranscript);
        Assert.Equal(3, ask.Lines!.Count);

        // And nothing offers to do what it cannot: both commands are dead rather than silently
        // no-op, which is what the buttons bound to them read.
        Assert.False(ask.PlayPauseCommand.CanExecute(null));
        Assert.False(ask.SeekToLineCommand.CanExecute(ask.Lines[0]));
    }

    [Fact]
    public void ThePlayButtonPlaysPausesAndStartsOverFromTheEnd()
    {
        var (ask, jobs, player) = Create();
        jobs.Add(Transcribed());

        Assert.False(ask.IsPlaying);
        Assert.Equal("Play", ask.PlayPauseLabel);

        ask.PlayPauseCommand.Execute(null);
        Assert.True(ask.IsPlaying);
        Assert.Equal("Pause", ask.PlayPauseLabel);

        ask.PlayPauseCommand.Execute(null);
        Assert.False(ask.IsPlaying);
        Assert.Equal("Play", ask.PlayPauseLabel);

        // Pressed at the end it wraps round. Without that the button is live, makes no sound and
        // looks broken.
        ask.SeekToFraction(1);
        Assert.Equal(player.Duration, player.Position);

        ask.PlayPauseCommand.Execute(null);
        Assert.True(ask.IsPlaying);
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [Fact]
    public void ClickingACueSeeksToItAndPlaysFromThere()
    {
        // The interaction the whole tab is for: a transcript you can click to hear. Seeks *and*
        // plays, because clicking a line is a request to hear it and a seek that leaves the
        // transport paused makes a reader press two things for one intention.
        var (ask, jobs, player) = Create();
        jobs.Add(Transcribed());

        var second = ask.Lines![1];
        Assert.Equal("00:10", second.Timestamp);

        ask.SeekToLineCommand.Execute(second);

        Assert.Equal(second.Start, player.Position);
        Assert.True(ask.IsPlaying);
        Assert.Same(second, ask.ActiveLine);
        Assert.True(second.IsActive);
    }

    [Fact]
    public void TheHighlightFollowsThePlayheadAndTouchesOnlyTheTwoLinesItHasTo()
    {
        var (ask, jobs, player) = Create();
        jobs.Add(Transcribed());
        ask.PlayPauseCommand.Execute(null);

        player.Advance(TimeSpan.FromSeconds(5));
        ask.Tick();
        Assert.Same(ask.Lines![0], ask.ActiveLine);

        // Two writes at most however long the transcript is: the line that had the highlight and
        // the line that takes it. Setting a flag on every line each tick would be fifteen hundred
        // notifications ten times a second on a three-hour recording.
        var changed = 0;
        foreach (var line in ask.Lines)
        {
            line.PropertyChanged += (_, _) => changed++;
        }

        player.Advance(TimeSpan.FromSeconds(10));
        ask.Tick();

        Assert.Same(ask.Lines[1], ask.ActiveLine);
        Assert.False(ask.Lines[0].IsActive);
        Assert.True(ask.Lines[1].IsActive);
        Assert.False(ask.Lines[2].IsActive);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void ATickThatFindsNothingMovedRaisesNothing()
    {
        // The window ticks this ten times a second for as long as it is open, on every tab. A tick
        // that redrew regardless would be eighty property notifications a second behind a window
        // where nothing is playing.
        var (ask, jobs, _) = Create();
        jobs.Add(Transcribed());

        var raised = 0;
        ask.PropertyChanged += (_, _) => raised++;

        ask.Tick();
        ask.Tick();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void ARecordingIsPlayableBeforeItIsTranscribedAndItsWordsFillInWhereTheyStand()
    {
        // The queue is shared with the Transcribe tab rather than copied, which is what makes this
        // work: the row filling in is the same object this tab is showing. Copied, the pane would
        // keep saying "not transcribed yet" over a finished transcript until the selection was
        // clicked away and back.
        var (ask, jobs, _) = Create();
        var job = new JobViewModel("/tmp/a.wav");
        jobs.Add(job);

        Assert.True(ask.CanPlay);
        Assert.False(ask.HasTranscript);
        Assert.Contains("has not been transcribed yet", ask.TranscriptNotice, StringComparison.Ordinal);

        Fill(job);

        Assert.True(ask.HasTranscript);
        Assert.Null(ask.TranscriptNotice);
        Assert.Equal(3, ask.Lines!.Count);
    }

    [Fact]
    public void TheCuesCarryThePipelinesOwnTimesRatherThanTimesTheWindowComputed()
    {
        // The rule docs/V2-ASK-THE-TRANSCRIPT.md sets for citations, kept where it is cheapest:
        // nothing in this window invents a timestamp. Every one of these is a TranscriptSegment's
        // own Start, unchanged.
        var (ask, jobs, _) = Create();
        jobs.Add(Transcribed());

        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)],
            ask.Lines!.Select(l => l.Start));

        Assert.Equal(["00:00", "00:10", "00:20"], ask.Lines!.Select(l => l.Timestamp));
    }

    [Fact]
    public void PressingTheBarSeeksToTheFractionOfTheWayAlongItThatWasPressed()
    {
        var (ask, jobs, player) = Create();
        jobs.Add(Transcribed());

        ask.SeekToFraction(0.5);
        Assert.Equal(TimeSpan.FromMinutes(1), player.Position);

        // Off either end of an 18px strip is a press that still means something sensible.
        ask.SeekToFraction(-3);
        Assert.Equal(TimeSpan.Zero, player.Position);

        ask.SeekToFraction(9);
        Assert.Equal(TimeSpan.FromMinutes(2), player.Position);
    }

    [Fact]
    public void TheBarsMaximumIsNeverZero()
    {
        // A ProgressBar whose maximum equals its minimum draws itself full, so an unopened
        // recording would show a finished one.
        var (ask, _, _) = Create();

        Assert.Equal(1, ask.DurationSeconds);
        Assert.Equal("00:00", ask.PositionLabel);
        Assert.Equal("00:00", ask.DurationLabel);
    }

    [Fact]
    public void DisposingTheTabReleasesTheDevice()
    {
        var (ask, jobs, player) = Create();
        jobs.Add(Transcribed());
        ask.PlayPauseCommand.Execute(null);

        ask.Dispose();

        Assert.Null(player.Path);
        Assert.False(player.IsPlaying);
    }

    // ── Finding a word ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATermMarksEveryLineThatCarriesItAndNoOthers()
    {
        var (ask, jobs, _) = Create();
        jobs.Add(Searchable());

        ask.SearchTerm = "tokon";

        Assert.Equal(2, ask.MatchCount);
        Assert.Equal("1 of 2", ask.SearchSummary);

        // Marked on the lines that carry it, and nowhere else. A search that wrote the term onto
        // every line would rebuild every paragraph in the transcript on every keystroke.
        Assert.Equal("tokon", ask.Lines![0].SearchTerm);
        Assert.Null(ask.Lines[1].SearchTerm);
        Assert.Equal("tokon", ask.Lines[2].SearchTerm);

        // It lands on the first hit, and says which one that is.
        Assert.Same(ask.Lines[0], ask.CurrentMatch);
        Assert.True(ask.Lines[0].IsCurrentMatch);
        Assert.False(ask.Lines[2].IsCurrentMatch);
        Assert.Equal(0, ask.CurrentMatchLineIndex);
    }

    [Fact]
    public void CaseIsIgnoredWhenLookingAndKeptWhenDrawing()
    {
        var (ask, jobs, _) = Create();
        jobs.Add(Searchable());

        ask.SearchTerm = "TOKON";

        Assert.Equal(2, ask.MatchCount);

        // What the line hands the view is its own text with the term beside it — never the term
        // written over the text. A transcript is what was said, and a search must not respell it.
        Assert.Equal("The Tokon report is late", ask.Lines![0].Marked.Text);
        Assert.Equal("TOKON", ask.Lines[0].Marked.Term);
    }

    [Fact]
    public void SteppingGoesForwardBackwardsAndRoundTheEnd()
    {
        var (ask, jobs, _) = Create();
        jobs.Add(Searchable());
        ask.SearchTerm = "tokon";

        Assert.Equal("1 of 2", ask.SearchSummary);

        ask.NextMatchCommand.Execute(null);
        Assert.Equal("2 of 2", ask.SearchSummary);
        Assert.Same(ask.Lines![2], ask.CurrentMatch);
        Assert.Equal(2, ask.CurrentMatchLineIndex);
        Assert.False(ask.Lines[0].IsCurrentMatch);

        // Round the end rather than dead at it. A find bar that stops makes the reader work out
        // where they are in the transcript they are searching precisely because they do not know.
        ask.NextMatchCommand.Execute(null);
        Assert.Equal("1 of 2", ask.SearchSummary);

        ask.PreviousMatchCommand.Execute(null);
        Assert.Equal("2 of 2", ask.SearchSummary);
    }

    [Fact]
    public void AnEmptyBoxIsNotASearchThatFoundNothing()
    {
        var (ask, jobs, _) = Create();
        jobs.Add(Searchable());

        // Nothing typed: nothing said, and nothing to step between.
        Assert.Null(ask.SearchSummary);
        Assert.False(ask.CanStepMatches);
        Assert.False(ask.NextMatchCommand.CanExecute(null));
        Assert.Equal(-1, ask.CurrentMatchLineIndex);

        ask.SearchTerm = "zzz";
        Assert.Equal("No matches", ask.SearchSummary);
        Assert.False(ask.CanStepMatches);
        Assert.Equal(-1, ask.CurrentMatchLineIndex);

        ask.SearchTerm = "tokon";
        Assert.True(ask.CanStepMatches);

        // And clearing it puts the transcript back exactly as it was.
        ask.SearchTerm = string.Empty;
        Assert.Null(ask.SearchSummary);
        Assert.All(ask.Lines!, line => Assert.Null(line.SearchTerm));
        Assert.All(ask.Lines!, line => Assert.False(line.IsCurrentMatch));
    }

    [Fact]
    public void NarrowingTheTermTakesTheMarkOffTheLinesThatNoLongerCarryIt()
    {
        var (ask, jobs, _) = Create();
        jobs.Add(Searchable());

        ask.SearchTerm = "t";
        Assert.Equal(3, ask.MatchCount);

        ask.SearchTerm = "tokon r";
        Assert.Equal(1, ask.MatchCount);
        Assert.Equal("tokon r", ask.Lines![0].SearchTerm);
        Assert.Null(ask.Lines[1].SearchTerm);
        Assert.Null(ask.Lines[2].SearchTerm);
    }

    [Fact]
    public void TheTermSurvivesAChangeOfRecordingAndIsRunAgainstTheNewOne()
    {
        // Somebody looking for a name across a session's worth of files is doing exactly that, and
        // clearing the box for them would mean typing it again for every file.
        var (ask, jobs, _) = Create();
        jobs.Add(Searchable("/tmp/a.wav"));
        ask.SearchTerm = "tokon";
        Assert.Equal(2, ask.MatchCount);

        jobs.Add(Transcribed("/tmp/b.wav"));
        ask.SelectedRecording = jobs[1];

        Assert.Equal("tokon", ask.SearchTerm);
        Assert.Equal(0, ask.MatchCount);
        Assert.Equal("No matches", ask.SearchSummary);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(7, "00:07")]
    [InlineData(1002, "16:42")]
    [InlineData(3600, "1:00:00")]
    [InlineData(10523, "2:55:23")]
    [InlineData(-4, "00:00")]
    public void ATimeIsWrittenTheSameWayOnACueAndOnTheClockBesideIt(int seconds, string expected) =>
        Assert.Equal(expected, Timecode.Format(TimeSpan.FromSeconds(seconds)));

    private static (AskViewModel Ask, ObservableCollection<JobViewModel> Jobs, FakeMediaPlayer Player) Create()
    {
        var jobs = new ObservableCollection<JobViewModel>();
        var player = new FakeMediaPlayer { DurationToReport = TimeSpan.FromMinutes(2) };
        return (new AskViewModel(jobs, player), jobs, player);
    }

    /// <summary>
    /// A transcript with a word to look for: twice in one line, once in another, and a line
    /// without it in between so a search has something to skip.
    /// </summary>
    internal static JobViewModel Searchable(string path = "/tmp/a.wav")
    {
        var job = new JobViewModel(path);

        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = path },
            State = JobState.Completed,
            Document = new TranscriptDocument
            {
                Segments =
                [
                    // Capitalised, so a lower-case search has to ignore case to find it and must
                    // not respell it on the page when it does.
                    new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "The Tokon report is late" },
                    new TranscriptSegment { Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(20), Text = "nothing to see" },
                    new TranscriptSegment { Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(30), Text = "tokon again, and tokon twice" },
                ],
            },
        });

        return job;
    }

    /// <summary>Three ten-second segments, which is enough for a highlight to move between them.</summary>
    internal static JobViewModel Transcribed(string path = "/tmp/a.wav")
    {
        var job = new JobViewModel(path);
        Fill(job);
        return job;
    }

    internal static void Fill(JobViewModel job) =>
        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = job.Path },
            State = JobState.Completed,
            Document = new TranscriptDocument
            {
                Segments =
                [
                    new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "one" },
                    new TranscriptSegment { Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(20), Text = "two" },
                    new TranscriptSegment { Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(30), Text = "three" },
                ],
            },
        });
}

/// <summary>
/// The same tab through the window that draws it, because every defect this application has had in
/// its interface was a control bound to nothing, and no view-model test can see one.
/// </summary>
public class AskTabWindowTests
{
    [AvaloniaFact]
    public void EveryCueIsDrawnAndWiredToTheSeek()
    {
        var (window, viewModel, player) = Open();

        var cues = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("cue"))
            .ToList();

        Assert.Equal(3, cues.Count);

        // The timestamp is on the cue, so what a reader clicks is the number they are aiming at.
        var labels = cues[1].GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();

        Assert.Contains("00:10", labels);

        // The words are drawn as runs rather than as Text, because one of them may have to carry a
        // search highlight — so the words are read the same way here.
        var words = cues[1].GetVisualDescendants().OfType<TextBlock>()
            .SelectMany(t => t.Inlines ?? [])
            .OfType<Run>()
            .Select(r => r.Text)
            .ToList();

        Assert.Contains("two", words);

        // Bound rather than merely present. A cue whose Command is null renders, hovers, and does
        // nothing — the exact shape of every interface defect this window has shipped.
        Assert.NotNull(cues[1].Command);
        Assert.Same(viewModel.Ask.Lines![1], cues[1].CommandParameter);

        cues[1].Command!.Execute(cues[1].CommandParameter);

        Assert.Equal(TimeSpan.FromSeconds(10), player.Position);
        Assert.True(player.IsPlaying);
    }

    [AvaloniaFact]
    public void TheTranscriptScrollsRatherThanClippingWhatDoesNotFit()
    {
        // A three-hour recording is fifteen hundred cues in a column three hundred pixels tall.
        // Without a scroller around them the tab shows the first few and hides the rest, which
        // looks exactly like a transcript that came back short.
        var (window, _, _) = Open();

        var cue = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("cue"));
        var scroller = cue.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();

        Assert.NotNull(scroller);
        Assert.NotEqual(ScrollBarVisibility.Disabled, scroller!.VerticalScrollBarVisibility);
    }

    [AvaloniaFact]
    public void PressingTheSeekBarMovesThePlayhead()
    {
        // Driven through the pointer rather than by calling the view model, because the whole
        // mechanism under test lives between the two: a press arrives as an x inside a control,
        // and only the control knows how wide it is.
        var (window, _, player) = Open();

        var strip = window.FindControl<Border>("SeekStrip");
        Assert.NotNull(strip);
        Assert.True(strip!.Bounds.Width > 0, "the seek strip was laid out with no width");

        var centre = strip.TranslatePoint(
            new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window);
        Assert.NotNull(centre);

        window.MouseDown(centre!.Value, MouseButton.Left);
        window.MouseUp(centre.Value, MouseButton.Left);
        window.UpdateLayout();

        // Half of two minutes, give or take the pixel the centre rounded to.
        Assert.InRange(player.Position.TotalSeconds, 57, 63);
    }

    [AvaloniaFact]
    public void TheChatPanelIsDrawnDisabledUnderANoticeSayingItIsNotBuilt()
    {
        // This window's standing rule is that it ships no control wired to nothing. The panel is
        // the deliberate exception, and these three assertions are what make it one rather than a
        // breach: nothing in it can be operated, the notice is over it, and the notice says so.
        var (window, _, _) = Open();

        var panel = window.FindControl<DockPanel>("AskPanel");
        var notice = window.FindControl<Border>("AskWorkInProgress");
        var input = window.FindControl<TextBox>("AskInput");
        var send = window.FindControl<Button>("AskSend");

        Assert.NotNull(panel);
        Assert.NotNull(notice);
        Assert.NotNull(input);
        Assert.NotNull(send);

        Assert.False(panel!.IsEnabled);
        Assert.False(input!.IsEffectivelyEnabled);
        Assert.False(send!.IsEffectivelyEnabled);
        Assert.True(notice!.IsVisible);

        var said = notice.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();

        Assert.Contains(said, t => t == "Work in progress");
        Assert.Contains(said, t => t is not null && t.Contains("is not built", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void TheFindBoxIsBoundBothWaysAndEnterStepsThroughTheHits()
    {
        var (window, viewModel, _) = Open(AskTabTests.Searchable());

        var box = window.FindControl<TextBox>("SearchBox");
        var summary = window.FindControl<TextBlock>("SearchSummary");
        Assert.NotNull(box);
        Assert.NotNull(summary);

        // Typed into the control, read by the view model: the half a view-model test cannot see.
        box!.Text = "tokon";
        window.UpdateLayout();

        Assert.Equal("tokon", viewModel.Ask.SearchTerm);
        Assert.Equal(2, viewModel.Ask.MatchCount);
        Assert.Equal("1 of 2", summary!.Text);

        // And Enter in the box steps, which is the gesture nobody looks for a button for. Driven
        // through the keyboard rather than by calling the command, because the handler that turns
        // one into the other is the thing under test.
        box.Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.UpdateLayout();

        Assert.Same(viewModel.Ask.Lines![2], viewModel.Ask.CurrentMatch);
        Assert.Equal("2 of 2", summary.Text);

        // Shift+Enter goes back.
        window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
        window.UpdateLayout();

        Assert.Same(viewModel.Ask.Lines[0], viewModel.Ask.CurrentMatch);
    }

    [AvaloniaFact]
    public void TheHitTheSearchIsStandingOnCanBeReachedAsARowToScrollTo()
    {
        // The one thing in this feature a view model cannot do. It publishes an index; the window
        // turns that into a container and calls BringIntoView on it. Asserting on a scroll offset
        // would be asserting on Avalonia's layout, so this asserts on the step in between — that
        // the list is findable by the name the window looks it up by, and that the index it is
        // handed resolves to the row carrying the hit.
        var (window, viewModel, _) = Open(AskTabTests.Searchable());

        viewModel.Ask.SearchTerm = "tokon";
        viewModel.Ask.NextMatchCommand.Execute(null);
        window.UpdateLayout();

        var cues = window.FindControl<ItemsControl>("Cues");
        Assert.NotNull(cues);

        var index = viewModel.Ask.CurrentMatchLineIndex;
        Assert.Equal(2, index);

        // A ContentPresenter, because this is a plain ItemsControl rather than a ListBox — which is
        // what BringIntoView is called on, and the cue is inside it.
        var container = cues!.ContainerFromIndex(index);
        Assert.NotNull(container);

        var cue = container!.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("cue"));
        Assert.Contains("found", cue.Classes);
    }

    [AvaloniaFact]
    public void TheWordIsPickedOutInsideTheLineWithoutBeingRespelled()
    {
        var (window, viewModel, _) = Open(AskTabTests.Searchable());

        viewModel.Ask.SearchTerm = "tokon";
        window.UpdateLayout();

        var cues = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("cue"))
            .ToList();

        var runs = cues[0].GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Inlines)
            .FirstOrDefault(i => i is { Count: > 1 });

        Assert.NotNull(runs);

        var texts = runs!.OfType<Run>().Select(r => r.Text).ToList();

        // Split around the hit, and the hit carries the transcript's own capital rather than the
        // lower-case letters that were typed to find it.
        Assert.Equal(["The ", "Tokon", " report is late"], texts);

        var marked = runs.OfType<Run>().Single(r => r.Text == "Tokon");
        var plain = runs.OfType<Run>().First(r => r.Text == "The ");

        Assert.Null(plain.Background);
        Assert.Equal(FontWeight.Bold, marked.FontWeight);

        // Taro-200, out of the token sheet rather than out of a hex in the converter. This also
        // proves the StaticResource in Window.Resources resolved: a converter whose brush stayed
        // null would draw an unmarked hit and look like a search that found nothing.
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(marked.Background);
        Assert.Equal(Color.Parse("#E3D0FE"), brush.Color);

        // The line with two occurrences gets both.
        var third = cues[2].GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Inlines)
            .First(i => i is { Count: > 1 });

        Assert.Equal(2, third!.OfType<Run>().Count(r => r.Background is not null));
    }

    [AvaloniaFact]
    public void TheCurrentHitIsMarkedApartFromTheRestWithoutMovingTheWords()
    {
        var (window, viewModel, _) = Open(AskTabTests.Searchable());

        var cues = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("cue"))
            .ToList();

        var before = cues[0].GetVisualDescendants().OfType<TextBlock>().First().Bounds;

        viewModel.Ask.SearchTerm = "tokon";
        window.UpdateLayout();

        Assert.Contains("found", cues[0].Classes);
        Assert.DoesNotContain("found", cues[2].Classes);

        // The edge that says which hit this is was always there and merely invisible, so the words
        // do not jog three pixels sideways every time the search steps onto their line.
        var after = cues[0].GetVisualDescendants().OfType<TextBlock>().First().Bounds;
        Assert.Equal(before.X, after.X, precision: 3);
    }

    [AvaloniaFact]
    public void TheFindBoxIsShutOnARecordingWithNoTranscriptToSearch()
    {
        // A box that takes a word and can never answer is worse than no box. It opens when the
        // words arrive, which is the same rule every other opt-in in this window follows.
        var (window, viewModel, _) = Open(new JobViewModel("/tmp/a.wav"));

        var box = window.FindControl<TextBox>("SearchBox");
        Assert.NotNull(box);
        Assert.False(box!.IsEffectivelyEnabled);

        AskTabTests.Fill(viewModel.Transcribe.Jobs[0]);
        window.UpdateLayout();

        Assert.True(box.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void TheTabIsDrawnInTaroAndNothingElseIs()
    {
        // The design's one structural rule: a surface's colour says which product generation it
        // belongs to. This holds the Ask tab to it from the only side a test can — the tokens
        // exist, and they are the ones the design's own arithmetic produced.
        var window = new MainWindow { DataContext = WindowTests.NewViewModel(out _) };
        window.Show();

        foreach (var (key, expected) in new[]
        {
            ("Taro50", "#F7F3FE"),
            ("Taro100", "#EFE5FE"),
            ("Taro200", "#E3D0FE"),
            ("Taro400", "#BFA1E6"),
            ("Taro600", "#7A619A"),
            ("Taro700", "#614B7C"),
        })
        {
            Assert.True(
                Application.Current!.TryFindResource(key, out var value),
                $"{key} is not defined");

            Assert.Equal(Color.Parse(expected), Assert.IsType<Color>(value));
        }
    }


    // ── The picture ───────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void TheVideoPaneIsDrawnOnlyForARecordingThatHasAPicture()
    {
        // A video pane over an audio file is dead glass. It exists exactly when frames do, which
        // is what the binding on HasVideo says and what this holds it to.
        var (window, viewModel, player) = Open();

        var pane = window.FindControl<Border>("VideoPane");
        Assert.NotNull(pane);
        Assert.False(pane!.IsVisible);

        // The same player, now reporting a picture on the next file it opens.
        player.VideoToReport = (640, 360);
        viewModel.Transcribe.Jobs.Add(AskTabTests.Transcribed("/tmp/clip.mp4"));
        viewModel.Ask.SelectedRecording = viewModel.Transcribe.Jobs[1];
        window.UpdateLayout();

        Assert.True(viewModel.Ask.HasVideo);
        Assert.True(pane.IsVisible);
    }

    [AvaloniaFact]
    public void AFrameReachesTheSurfaceAsABitmapOfItsOwnSize()
    {
        // The one path in this tab that is not a binding: frames arrive on the decoder's thread and
        // are blitted into a WriteableBitmap by the code-behind. What is asserted is the end of
        // that path — a bitmap on the Image, sized to the frame, with the copy having been made.
        var player = new FakeMediaPlayer
        {
            DurationToReport = TimeSpan.FromMinutes(2),
            VideoToReport = (320, 180),
        };

        var (window, _, _) = Open(AskTabTests.Transcribed("/tmp/clip.mp4"), player);

        var surface = window.FindControl<Image>("VideoSurface");
        Assert.NotNull(surface);
        Assert.Null(surface!.Source);

        player.RaiseFrame();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var bitmap = Assert.IsType<WriteableBitmap>(surface.Source);
        Assert.Equal(new PixelSize(320, 180), bitmap.PixelSize);
        Assert.Equal(1, player.FramesCopied);
    }

    [AvaloniaFact]
    public void ThePaneTellsThePlayerHowLargeItIsSoFramesAreNotRenderedTwiceTheSizeTheyAreShown()
    {
        // Rendering at the file's own size and letting the compositor shrink it is sixteen times
        // the pixels for nothing on a 4K recording in a 600-pixel pane. Only a laid-out control
        // knows the size, which is why this is wired in the code-behind and asserted here.
        var player = new FakeMediaPlayer
        {
            DurationToReport = TimeSpan.FromMinutes(2),
            VideoToReport = (1920, 1080),
        };

        var (window, _, _) = Open(AskTabTests.Transcribed("/tmp/clip.mp4"), player);
        window.UpdateLayout();

        Assert.NotNull(player.RequestedOutputSize);
        Assert.True(player.RequestedOutputSize!.Value.Width > 0);
        Assert.True(player.RequestedOutputSize.Value.Height > 0);
    }

    [AvaloniaFact]
    public void AnAudioOnlyBuildSaysSoOnAVideoFileAndSaysNothingOnAnAudioOne()
    {
        // The graceful half of "video is a property of the build": a build with no libmpv still
        // plays a video's sound, and the tab says why there is no picture rather than leaving a
        // blank where one should be. On an audio file there is nothing to explain, and a notice
        // there would be noise.
        var player = new FakeMediaPlayer
        {
            DurationToReport = TimeSpan.FromMinutes(2),
            CanDrawVideo = false,
        };

        var (window, viewModel, _) = Open(AskTabTests.Transcribed("/tmp/clip.mp4"), player);

        var notice = window.FindControl<TextBlock>("VideoNotice");
        Assert.NotNull(notice);
        Assert.True(notice!.IsVisible);
        Assert.Contains("no video player", notice.Text, StringComparison.Ordinal);

        // The sound still plays: the transport is live, and the pane is simply absent.
        Assert.True(viewModel.Ask.CanPlay);
        Assert.False(viewModel.Ask.HasVideo);
        Assert.False(window.FindControl<Border>("VideoPane")!.IsVisible);

        viewModel.Transcribe.Jobs.Add(AskTabTests.Transcribed("/tmp/talk.mp3"));
        viewModel.Ask.SelectedRecording = viewModel.Transcribe.Jobs[1];
        window.UpdateLayout();

        Assert.Null(viewModel.Ask.VideoNotice);
        Assert.False(notice.IsVisible);
    }

    /// <summary>The window, on the Ask tab, with one recording in the queue.</summary>
    private static (MainWindow Window, MainWindowViewModel ViewModel, FakeMediaPlayer Player) Open(
        JobViewModel? recording = null,
        FakeMediaPlayer? withPlayer = null)
    {
        var player = withPlayer ?? new FakeMediaPlayer { DurationToReport = TimeSpan.FromMinutes(2) };
        var directory = Directory.CreateTempSubdirectory("uindosill-ask").FullName;
        var viewModel = new MainWindowViewModel(
            new FakeEngineProvider(),
            new LocalModelStore(directory),
            ModelCatalog.Default,
            player: player);

        viewModel.Transcribe.Jobs.Add(recording ?? AskTabTests.Transcribed());

        // Four before it in the TabControl; the switcher shows it second. A TabControl only
        // realises the selected page, so the tab has to be current before a layout pass puts any
        // of this in the visual tree.
        viewModel.SelectedTab = 4;

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        return (window, viewModel, player);
    }
}
