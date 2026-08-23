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
        // On a transcript with no word timings, so that what is counted below is the line
        // highlight alone. `Spoken()` is the fixture that carries words, and the mark inside a
        // line is held to the same bound a few tests further down.
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
    public void TheWordBeingSaidIsMarkedInsideTheLineBeingPlayed()
    {
        // The mark the design pins the pastel yellow to, on v1's own data: the engine's word
        // timings, which every transcription run already writes and `vtt-words` already exports.
        var (ask, jobs, player) = Create();
        jobs.Add(Spoken());
        ask.PlayPauseCommand.Execute(null);

        var first = ask.Lines![0];
        Assert.True(first.HasWordTimings);

        // Inside the line and before its first word. The line is lit and no word is: nothing is
        // drawn ahead of the moment being played.
        player.Advance(TimeSpan.FromSeconds(0.5));
        ask.Tick();
        Assert.Same(first, ask.ActiveLine);
        Assert.Null(MarkedWord(first));

        player.Advance(TimeSpan.FromSeconds(1));
        ask.Tick();
        Assert.Equal("one", MarkedWord(first));

        player.Advance(TimeSpan.FromSeconds(2));
        ask.Tick();
        Assert.Equal("two", MarkedWord(first));

        // 4.5s: "two" has ended and "three" has not begun. The mark holds across the gap rather
        // than blinking off — at a tick every 100 ms a rule that lit nothing between words would
        // flicker through every sentence and go dark at every pause.
        player.Advance(TimeSpan.FromSeconds(1));
        ask.Tick();
        Assert.Equal("two", MarkedWord(first));

        player.Advance(TimeSpan.FromSeconds(1));
        ask.Tick();
        Assert.Equal("three", MarkedWord(first));

        // And it is where the word is rather than where a word of that spelling first appears:
        // "three" is the eighth character in, not the nought-th.
        Assert.Equal(8, first.Marked.SpokenStart);
    }

    [Fact]
    public void TheMarkLeavesTheLineThePlayheadHasLeft()
    {
        var (ask, jobs, player) = Create();
        jobs.Add(Spoken());
        ask.PlayPauseCommand.Execute(null);

        player.Advance(TimeSpan.FromSeconds(3.5));
        ask.Tick();

        var first = ask.Lines![0];
        Assert.Equal("two", MarkedWord(first));

        player.Advance(TimeSpan.FromSeconds(7));
        ask.Tick();

        Assert.Same(ask.Lines[1], ask.ActiveLine);
        Assert.Equal("four", MarkedWord(ask.Lines[1]));

        // Nothing left behind: one word lit inside a paragraph nobody is inside reads as a
        // highlight that has got stuck rather than as a line that has been played.
        Assert.Equal(-1, first.SpokenWord);
        Assert.Null(MarkedWord(first));
    }

    [Fact]
    public void SeekingBackwardsPutsTheMarkBackWhereThePlayheadIs()
    {
        // The mark is computed from the position every time rather than stepped forward, so a
        // click on an earlier cue — or a press on the seek bar — moves it back with everything
        // else instead of stranding it ahead of what is being played.
        var (ask, jobs, player) = Create();
        jobs.Add(Spoken());
        ask.PlayPauseCommand.Execute(null);

        player.Advance(TimeSpan.FromSeconds(5.5));
        ask.Tick();
        Assert.Equal("three", MarkedWord(ask.Lines![0]));

        ask.SeekToLineCommand.Execute(ask.Lines[0]);
        Assert.Null(MarkedWord(ask.Lines[0]));

        player.Advance(TimeSpan.FromSeconds(1.5));
        ask.Tick();
        Assert.Equal("one", MarkedWord(ask.Lines[0]));
    }

    [Fact]
    public void ATranscriptWithNoWordTimingsMarksNoWordAndFollowsTheLineExactlyAsBefore()
    {
        // What a translated pane is, and what any engine that reports no word timings produces.
        // The line highlight is untouched by the absence; no word is marked, and none is guessed
        // from how far through the line the playhead is — which is the guess `WordTimedVttFormatter`
        // refuses to write, calling it worthless about when a word is spoken.
        var (ask, jobs, player) = Create();
        jobs.Add(Transcribed());
        ask.PlayPauseCommand.Execute(null);

        player.Advance(TimeSpan.FromSeconds(5));
        ask.Tick();

        var line = ask.Lines![0];
        Assert.Same(line, ask.ActiveLine);
        Assert.True(line.IsActive);
        Assert.False(line.HasWordTimings);
        Assert.Equal(-1, line.SpokenWord);
        Assert.Null(MarkedWord(line));
    }

    [Fact]
    public void AWordThatDoesNotSpellTheLineIsSkippedRatherThanPutSomewhereItIsNot()
    {
        // Joining the words with single spaces reproduces the segment's text on nearly every
        // segment this pipeline produces, and `SpeakerAssignment` checks exactly that before it
        // cuts one — but nearly is not always, and assuming it fails silently: every word after
        // the first disagreement lights one word early, which looks like a transcript rather than
        // like a defect. So each word is found in the text or skipped.
        var job = new JobViewModel("/tmp/a.wav");

        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = "/tmp/a.wav" },
            State = JobState.Completed,
            Document = new TranscriptDocument
            {
                Segments =
                [
                    new TranscriptSegment
                    {
                        Start = TimeSpan.Zero,
                        End = TimeSpan.FromSeconds(10),
                        Text = "the tokon report",
                        Words = [Word("the", 0, 1), Word("Tokon", 1, 2), Word("report", 2, 3)],
                    },
                ],
            },
        });

        var (ask, jobs, player) = Create();
        jobs.Add(job);
        ask.PlayPauseCommand.Execute(null);

        var line = ask.Lines![0];

        player.Advance(TimeSpan.FromSeconds(0.5));
        ask.Tick();
        Assert.Equal("the", MarkedWord(line));

        // The word the engine spelled differently is never marked, and the mark holds on the one
        // before it rather than sliding onto the wrong word for a second.
        player.Advance(TimeSpan.FromSeconds(1));
        ask.Tick();
        Assert.Equal("the", MarkedWord(line));

        // And the word after it still lands on its own characters rather than one word out.
        player.Advance(TimeSpan.FromSeconds(1));
        ask.Tick();
        Assert.Equal("report", MarkedWord(line));
        Assert.Equal(10, line.Marked.SpokenStart);
    }

    [Fact]
    public void AWordAdvancingTouchesOneLineAndTheHighlightMovingTouchesTwo()
    {
        // The same bound the line highlight is held to, extended to the mark inside it. The word
        // moves several times a second for as long as a recording plays, so what it costs per tick
        // is the whole question: one line's worth of notifications, never the transcript's.
        var (ask, jobs, player) = Create();
        jobs.Add(Spoken());
        ask.PlayPauseCommand.Execute(null);

        player.Advance(TimeSpan.FromSeconds(1.5));
        ask.Tick();

        var counts = new int[ask.Lines!.Count];

        for (var i = 0; i < ask.Lines.Count; i++)
        {
            var at = i;
            ask.Lines[i].PropertyChanged += (_, _) => counts[at]++;
        }

        // A word advancing inside the line being played: one line, and the two facts that moved
        // on it — which word it is, and the marks the view draws from it.
        player.Advance(TimeSpan.FromSeconds(2));
        ask.Tick();

        Assert.Equal(new[] { 2, 0, 0 }, counts);

        // The highlight moving to the next line: two lines, three facts each — whether it is the
        // line being played, which word it has or no longer has, and the marks.
        player.Advance(TimeSpan.FromSeconds(7));
        ask.Tick();

        Assert.Equal(new[] { 5, 3, 0 }, counts);
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

    /// <summary>
    /// The same three segments carrying the word timings the engine reports, which is what the
    /// mark on the word being said is drawn from.
    /// </summary>
    /// <remarks>
    /// Deliberately not wall-to-wall words. The first line has half a second of silence in front
    /// of its first word and a second of it between two others, because both are places the mark
    /// has to behave — nothing lit before the first word, and the last word held across the gap
    /// rather than blinking off.
    /// </remarks>
    internal static JobViewModel Spoken(string path = "/tmp/a.wav")
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
                    new TranscriptSegment
                    {
                        Start = TimeSpan.Zero,
                        End = TimeSpan.FromSeconds(10),
                        Text = "one two three",
                        Words = [Word("one", 1, 2), Word("two", 3, 4), Word("three", 5, 6)],
                    },
                    new TranscriptSegment
                    {
                        Start = TimeSpan.FromSeconds(10),
                        End = TimeSpan.FromSeconds(20),
                        Text = "four five",
                        Words = [Word("four", 10, 11), Word("five", 11, 12)],
                    },
                    new TranscriptSegment
                    {
                        Start = TimeSpan.FromSeconds(20),
                        End = TimeSpan.FromSeconds(30),
                        Text = "six",
                        Words = [Word("six", 20, 21)],
                    },
                ],
            },
        });

        return job;
    }

    /// <summary>
    /// Sixty ten-second segments — ten minutes, and more transcript than the pane can show, which
    /// is what the pane following the playhead needs in order to have somewhere to scroll to.
    /// </summary>
    internal static JobViewModel Long(int count = 60)
    {
        var segments = new List<TranscriptSegment>(count);

        for (var i = 0; i < count; i++)
        {
            var at = i * 10;

            segments.Add(new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(at),
                End = TimeSpan.FromSeconds(at + 10),
                Text = $"line {i}",
                Words = [Word("line", at, at + 1), Word($"{i}", at + 1, at + 2)],
            });
        }

        var job = new JobViewModel("/tmp/long.wav");

        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = "/tmp/long.wav" },
            State = JobState.Completed,
            Document = new TranscriptDocument { Segments = segments },
        });

        return job;
    }

    private static TranscriptWord Word(string text, double from, double to) => new()
    {
        Text = text,
        Start = TimeSpan.FromSeconds(from),
        End = TimeSpan.FromSeconds(to),
    };

    /// <summary>
    /// The word <paramref name="line"/> says is being spoken, read back out of the marks the view
    /// draws rather than off the index behind them — which is the fact that matters, and the one
    /// that would be wrong if a word were located in the wrong place in the text.
    /// </summary>
    private static string? MarkedWord(TranscriptLineViewModel line) =>
        line.Marked.SpokenLength == 0
            ? null
            : line.Text.Substring(line.Marked.SpokenStart, line.Marked.SpokenLength);

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
    public void TheWordBeingSaidCarriesThePastelYellowAndNoChangeOfWeight()
    {
        var (window, viewModel, player) = Open(AskTabTests.Spoken());

        viewModel.Ask.PlayPauseCommand.Execute(null);
        player.Advance(TimeSpan.FromSeconds(3.5));
        viewModel.Ask.Tick();
        window.UpdateLayout();

        var runs = Runs(window, 0);

        // Cut around the word being said and nowhere else, so the rest of the paragraph is one
        // run and stays one run as the mark walks through it.
        Assert.Equal(["one ", "two", " three"], runs.Select(r => r.Text));

        var marked = Assert.Single(runs, r => r.Background is not null);
        Assert.Equal("two", marked.Text);

        // #F0D983, out of the token sheet rather than out of a hex in the converter — the design
        // pins that colour to this one job. Reading it here also proves the StaticResource
        // resolved: a converter whose brush stayed null would draw an unmarked word and look like
        // a mark that never arrived.
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(marked.Background);
        Assert.Equal(Color.Parse("#F0D983"), brush.Color);

        // A ground and nothing else. A weight would change the word's width three times a second
        // and re-wrap the paragraph under the reader.
        Assert.NotEqual(FontWeight.Bold, marked.FontWeight);
    }

    [AvaloniaFact]
    public void AWordThatIsBothTheHitAndTheOneBeingSaidTakesTheSpokenGroundAndKeepsTheHitsWeight()
    {
        // The two marks are independent and land on the same word here. The ground can only be
        // one of them and it is the spoken one, because that is the mark that is moving; the hit
        // is still legible because weight is the other half of how a hit is drawn.
        var (window, viewModel, player) = Open(AskTabTests.Spoken());

        viewModel.Ask.SearchTerm = "two";
        viewModel.Ask.PlayPauseCommand.Execute(null);
        player.Advance(TimeSpan.FromSeconds(3.5));
        viewModel.Ask.Tick();
        window.UpdateLayout();

        var marked = Assert.Single(Runs(window, 0), r => r.Text == "two");

        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(marked.Background);
        Assert.Equal(Color.Parse("#F0D983"), brush.Color);
        Assert.Equal(FontWeight.Bold, marked.FontWeight);
    }

    [AvaloniaFact]
    public void ALineNobodyIsInsideIsDrawnPlainHoweverFarThroughTheRecordingItIs()
    {
        // The mark exists on exactly one line at a time. Drawn on the line ahead as well — or
        // left on the line behind — it would say two places in the recording are being played.
        var (window, viewModel, player) = Open(AskTabTests.Spoken());

        viewModel.Ask.PlayPauseCommand.Execute(null);
        player.Advance(TimeSpan.FromSeconds(3.5));
        viewModel.Ask.Tick();
        window.UpdateLayout();

        Assert.Single(Runs(window, 0), r => r.Background is not null);

        // One run apiece, and no ground on it: a line nobody is inside is not cut up at all.
        Assert.Equal(["four five"], Runs(window, 1).Select(r => r.Text));
        Assert.Equal(["six"], Runs(window, 2).Select(r => r.Text));

        player.Advance(TimeSpan.FromSeconds(7));
        viewModel.Ask.Tick();
        window.UpdateLayout();

        // And the line the playhead has left goes back to being one of them.
        Assert.Equal(["one two three"], Runs(window, 0).Select(r => r.Text));
        Assert.Single(Runs(window, 1), r => r.Background is not null);
    }

    /// <summary>The runs the cue at <paramref name="index"/> draws its words as.</summary>
    private static List<Run> Runs(MainWindow window, int index)
    {
        var cue = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("cue"))
            .ElementAt(index);

        // The paragraph, rather than the timestamp beside it: only the words are drawn as inlines
        // through the converter, and only they are ever cut into more than one run.
        return cue.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Name == "CueWords")
            .SelectMany(t => t.Inlines?.OfType<Run>() ?? [])
            .ToList();
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


    [AvaloniaFact]
    public void ThePaneFollowsTheLineBeingPlayed()
    {
        // Without this the mark on the word being said is correct and invisible: a pane that does
        // not move puts the line being played off the top within a minute of pressing play.
        var (window, viewModel, player, scroller) = OpenLong();

        Assert.Equal(0, scroller.Offset.Y);

        viewModel.Ask.PlayPauseCommand.Execute(null);
        Advance(window, viewModel, player, TimeSpan.FromMinutes(5));

        Assert.Equal(30, viewModel.Ask.ActiveLineIndex);
        Assert.True(scroller.Offset.Y > 0, "the pane should have moved to the line being played");
        Assert.True(PlayedLineInView(window, viewModel), "the line being played should be in view");
    }

    [AvaloniaFact]
    public void ThePaneStopsFollowingOnceTheReaderHasScrolledAwayAndPicksItUpAgainAfterwards()
    {
        // The rule, and the reason it needs no gesture to detect and no flag to reset: the pane
        // follows while the line it last left is on screen. A reader who has scrolled somewhere
        // else is reading, and taking the page back off them every ten seconds would be this
        // window arguing with them.
        var (window, viewModel, player, scroller) = OpenLong();

        viewModel.Ask.PlayPauseCommand.Execute(null);
        Advance(window, viewModel, player, TimeSpan.FromMinutes(5));
        Assert.True(PlayedLineInView(window, viewModel));

        // The reader goes and reads the end of the transcript.
        scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
        window.UpdateLayout();

        var parked = scroller.Offset.Y;
        Assert.False(PlayedLineInView(window, viewModel));

        Advance(window, viewModel, player, TimeSpan.FromSeconds(10));

        Assert.Equal(31, viewModel.Ask.ActiveLineIndex);
        Assert.Equal(parked, scroller.Offset.Y);

        // And back: the reader scrolls to the top and clicks the first cue, which seeks there and
        // is a request to be there. Following picks up from the next line on, with nothing reset.
        scroller.Offset = default;
        window.UpdateLayout();
        viewModel.Ask.SeekToLineCommand.Execute(viewModel.Ask.Lines![0]);
        window.UpdateLayout();

        Advance(window, viewModel, player, TimeSpan.FromMinutes(5));

        Assert.Equal(30, viewModel.Ask.ActiveLineIndex);
        Assert.True(scroller.Offset.Y > 0);
        Assert.True(PlayedLineInView(window, viewModel));
    }

    /// <summary>The window on a transcript longer than the pane, with the scroller in hand.</summary>
    private static (MainWindow Window, MainWindowViewModel ViewModel, FakeMediaPlayer Player, ScrollViewer Scroller) OpenLong()
    {
        var player = new FakeMediaPlayer { DurationToReport = TimeSpan.FromMinutes(20) };
        var (window, viewModel, _) = Open(AskTabTests.Long(), player);

        var scroller = window.FindControl<ScrollViewer>("CueScroll");
        Assert.NotNull(scroller);

        // The premise of both tests above, asserted rather than assumed: a transcript that fits
        // the pane has nowhere to scroll, and every assertion about scrolling would pass on it.
        Assert.True(
            scroller!.Extent.Height > scroller.Viewport.Height,
            "the fixture has to be longer than the pane or there is nothing to follow");

        return (window, viewModel, player, scroller);
    }

    /// <summary>Moves the clock on and lets the window do everything the moment would make it do.</summary>
    private static void Advance(
        MainWindow window,
        MainWindowViewModel viewModel,
        FakeMediaPlayer player,
        TimeSpan elapsed)
    {
        player.Advance(elapsed);
        viewModel.Ask.Tick();

        // The scroll is posted at background priority, because the container for a row may not
        // exist when the index changes. Nothing has moved until the post has run.
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>Whether any part of the line being played is inside the pane.</summary>
    private static bool PlayedLineInView(MainWindow window, MainWindowViewModel viewModel)
    {
        var cues = window.FindControl<ItemsControl>("Cues");
        var scroller = window.FindControl<ScrollViewer>("CueScroll");

        if (cues?.ContainerFromIndex(viewModel.Ask.ActiveLineIndex) is not Control container
            || scroller is null)
        {
            return false;
        }

        return container.TranslatePoint(default, scroller) is { } top
            && top.Y + container.Bounds.Height > 0
            && top.Y < scroller.Bounds.Height;
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
