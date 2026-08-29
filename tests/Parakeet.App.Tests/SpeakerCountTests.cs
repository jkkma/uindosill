using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

/// <summary>
/// The window's half of the speaker count, and of the warning that says why somebody would want to
/// give one.
/// </summary>
/// <remarks>
/// <para>
/// Both were on the command line and neither was here, which is a worse gap than it sounds. The
/// shipping diariser's labels are established up to fifty minutes and over-segment past about an
/// hour — one host heard as two — and the count is the only repair for that, because merging two
/// labels is possible where splitting one back into two people is not. A two-hour recording put
/// through this window came back with speaker names, no warning that they were past where the
/// evidence stops, and no control that would have fixed them.
/// </para>
/// <para>
/// The canned labeller stands in for the real one's shape rather than its weights: a cap, a length
/// its labels are established to, and — the property that matters — no way to be told a count, so
/// the fold downstream is what has to honour it. That is the arrangement these tests exercise, and
/// it is the shipping one.
/// </para>
/// </remarks>
public class SpeakerCountTests
{
    /// <summary>
    /// A window whose labeller has the shipping one's shape. <paramref name="speakers"/> is how many
    /// voices the canned labeller invents, and it cannot be told otherwise — which is exactly the
    /// over-segmentation the count exists to repair.
    /// </summary>
    private static (TranscribeViewModel ViewModel, string Directory) Create(
        FakeSpeakerLabellerOptions speakers)
    {
        var directory = TestTemp.NewDirectory("uindosill-spk");
        var main = new MainWindowViewModel(
            new FakeEngineProvider(speakers: speakers), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        main.Transcribe.OutputDirectory = directory;

        main.Session.LoadAsync(new EngineSelection { Model = main.Models.SelectedDescriptor })
            .GetAwaiter().GetResult();

        return (main.Transcribe, directory);
    }

    /// <summary>
    /// Four voices taking one-second turns that lap a quarter of a second into each other, and a
    /// labeller that cannot be told otherwise.
    /// </summary>
    /// <remarks>
    /// The overlap is what gives the fold something to reason about rather than a table of zeroes.
    /// Turns that are adjacent in time collide; turns two apart never do — so <c>SPEAKER_00</c> and
    /// <c>SPEAKER_02</c> are the pair that never talk over each other, which is the signature of one
    /// person's identity having drifted onto a second label and precisely what the fold looks for.
    /// </remarks>
    private static FakeSpeakerLabellerOptions OverSegmenting => new()
    {
        SpeakerCount = 4,
        TurnLength = TimeSpan.FromSeconds(1),
        Overlap = TimeSpan.FromSeconds(0.25),
        SupportsFixedSpeakerCount = false,
        MaxSpeakers = 4,
    };

    private static string WriteWav(string directory, string name, double seconds = 8, int sampleRate = 16_000)
    {
        var path = Path.Combine(directory, name);
        var samples = new float[(int)(sampleRate * seconds)];
        var random = new Random(5);

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 200 * i / sampleRate)
                + (random.NextDouble() * 0.001 - 0.0005));
        }

        WavWriter.WriteFile(path, samples, sampleRate);
        return path;
    }

    private static void SelectTextAndTurns(TranscribeViewModel viewModel)
    {
        foreach (var format in viewModel.Formats)
        {
            format.IsSelected = format.Id is "txt" or "rttm";
        }
    }

    /// <summary>How many distinct voices a run actually wrote, read back off its own RTTM.</summary>
    private static int SpeakersIn(string path) =>
        SpeakerTurns.Speakers(RttmFile.Parse(File.ReadAllText(path)).Turns).Count;

    [Fact]
    public void TheFieldIsBlankByDefaultAndOnlyLivesWithTheOptIn()
    {
        var (viewModel, _) = Create(OverSegmenting);

        // Blank by default rather than defaulting to two: the number has to come from the user for
        // the fold to mean anything, since a guessed count would merge two genuinely different
        // speakers on one of the eighteen AMI development meetings. Blank runs, and the hint says
        // what blank does rather than demanding a number the model is never told.
        Assert.Null(viewModel.SpeakerCount);
        Assert.False(viewModel.CanSetSpeakerCount);
        Assert.Null(viewModel.SpeakerCountHint);

        viewModel.LabelSpeakers = true;
        Assert.True(viewModel.CanSetSpeakerCount);
        Assert.Contains("Leave this blank", viewModel.SpeakerCountHint, StringComparison.Ordinal);
        Assert.DoesNotContain("does not run without it", viewModel.SpeakerCountHint, StringComparison.Ordinal);

        // And it says the count will be honoured afterwards rather than by the model, because those
        // are different facts and only one of them is what happens.
        viewModel.SpeakerCount = 2;
        Assert.Contains("folded down to 2 afterwards", viewModel.SpeakerCountHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutACountTheBatchRunsAndTheModelDecides()
    {
        // **This refused to start until 2026-08-28, and the refusal was the retired model's.** It
        // was argued from a fifty-minute bound past which that model's estimate drifted; the figure
        // went to attic/ with the model, and the labeller that ships declares no bound and no cap.
        // The count never reaches the clustering in either case, so requiring it did not steer the
        // model — it forced a fold, and below the true count a fold merges two real people.
        var (viewModel, directory) = Create(OverSegmenting);
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectTextAndTurns(viewModel);
        viewModel.LabelSpeakers = true;

        await viewModel.StartCommand.ExecuteAsync(null);

        // It runs, and what comes back is the model's own estimate — four, unfolded, because
        // nothing asked for fewer.
        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.DoesNotContain("needs to know how many", viewModel.StatusMessage, StringComparison.Ordinal);

        viewModel.SelectedJob = viewModel.Jobs[0];
        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        Assert.Equal(4, SpeakersIn(Path.Combine(directory, "a.rttm")));
    }

    [Fact]
    public async Task ACountTheLabellerCannotBeToldIsFoldedDownAfterwards()
    {
        var (viewModel, directory) = Create(OverSegmenting);
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectTextAndTurns(viewModel);
        viewModel.LabelSpeakers = true;
        viewModel.SpeakerCount = 2;

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);

        // The same four labels, folded to the two that were asked for. The model was never told
        // anything: it produced four and the repair ran on its output. Read back off the RTTM the
        // Export button writes, since a run writes nothing itself.
        viewModel.SelectedJob = viewModel.Jobs[0];
        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        Assert.Equal(2, SpeakersIn(Path.Combine(directory, "a.rttm")));

        // And the merges are reported rather than made quietly, each with the seconds the pair
        // collided for and how far behind the next-closest pair was. A merge the user's own number
        // forced is still a merge they are owed an account of.
        var warning = viewModel.Jobs[0].Warning;
        Assert.Contains("Folded to the speaker count you asked for", warning, StringComparison.Ordinal);
        Assert.Contains("the next-closest pair overlapped", warning, StringComparison.Ordinal);
        Assert.Contains("put two people under one name", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACountTheLabellerCanBeToldReachesTheModelInstead()
    {
        // The other half of the seam, and the reason the capability exists rather than a guess: a
        // labeller that takes a count is given one, and nothing is folded.
        var (viewModel, directory) = Create(new FakeSpeakerLabellerOptions
        {
            SpeakerCount = 4,
            TurnLength = TimeSpan.FromSeconds(1),
            SupportsFixedSpeakerCount = true,
        });

        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectTextAndTurns(viewModel);
        viewModel.LabelSpeakers = true;
        viewModel.SpeakerCount = 3;

        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.SelectedJob = viewModel.Jobs[0];
        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        Assert.Equal(3, SpeakersIn(Path.Combine(directory, "a.rttm")));
        Assert.DoesNotContain("Folded", viewModel.Jobs[0].Warning ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRowNamesWhichBackendProducedTheSpeakerLabels()
    {
        // The window named the backend the *transcription* ran on and never the one the labels came
        // from, so a GPU diarisation and a CPU one were indistinguishable here — in a product whose
        // rule is that a figure is never quoted without its backend. WebGPU is the case that was
        // silent: DescribeBackend returns a sentence only for the two backends that do not
        // reproduce the published figure, and this is one of the two that do.
        var (viewModel, directory) = Create(OverSegmenting with { Backend = ComputeBackend.WebGpu });

        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectTextAndTurns(viewModel);
        viewModel.LabelSpeakers = true;
        viewModel.SpeakerCount = 4;

        await viewModel.StartCommand.ExecuteAsync(null);

        var job = viewModel.Jobs[0];
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("Speakers: fake-speakers on webgpu", job.SpeakerProvenance);

        // And it stays out of the warning line, which is reserved for things that need attention:
        // a sentence there on every run about a backend that agrees is what trains people to
        // ignore the line that matters.
        Assert.DoesNotContain("webgpu", job.Warning ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunWithoutSpeakersClaimsNoLabellingBackend()
    {
        // No labels, nothing to attribute. The line is absent rather than saying "none".
        var (viewModel, directory) = Create(OverSegmenting);

        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        viewModel.LabelSpeakers = false;

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.Null(viewModel.Jobs[0].SpeakerProvenance);
    }

    [Fact]
    public void ARecordingPastWhereTheEvidenceStopsSaysSoBeforeAnythingRuns()
    {
        var (viewModel, directory) = Create(OverSegmenting with { ReliableUpTo = TimeSpan.FromMinutes(2) });

        // Nothing is decoded here on purpose. The point of this warning is that it is readable while
        // there is still a decision to make — the length comes off the file's header when it was
        // queued, which is what the command line relies on to warn before it reads a sample.
        viewModel.AddFiles([WriteWav(directory, "long.wav", seconds: 190, sampleRate: 8_000)]);
        Assert.Equal(TimeSpan.FromSeconds(190), viewModel.Jobs[0].Duration);

        // Off, it says nothing: a transcript with no speaker labels cannot have unestablished ones.
        Assert.Null(viewModel.SpeakerDurationWarning);

        viewModel.LabelSpeakers = true;
        var warning = viewModel.SpeakerDurationWarning;
        Assert.StartsWith("long.wav: this recording is 3 minutes", warning, StringComparison.Ordinal);
        Assert.Contains("treat the names as a guess", warning, StringComparison.Ordinal);
        Assert.Contains("the words are unaffected", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortRecordingIsInsideTheBoundAndSaysNothing()
    {
        var (viewModel, directory) = Create(OverSegmenting with { ReliableUpTo = TimeSpan.FromMinutes(2) });

        viewModel.AddFiles([WriteWav(directory, "short.wav")]);
        viewModel.LabelSpeakers = true;

        Assert.Null(viewModel.SpeakerDurationWarning);

        // A second file past the bound raises it, and the sentence names the longest rather than
        // whichever happened to be dropped last.
        viewModel.AddFiles([WriteWav(directory, "longest.wav", seconds: 400, sampleRate: 8_000)]);
        viewModel.AddFiles([WriteWav(directory, "long.wav", seconds: 190, sampleRate: 8_000)]);

        Assert.StartsWith("2 of the files queued are longer", viewModel.SpeakerDurationWarning, StringComparison.Ordinal);
        Assert.Contains("The longest, longest.wav:", viewModel.SpeakerDurationWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWhoseHeaderWillNotReadIsQueuedAnyway()
    {
        // Being unreadable is a real result and the run reports it per file, with the row failed and
        // the batch untouched. Refusing it at the door would turn one broken header into a file the
        // user cannot even queue, over a probe nothing downstream depends on.
        var (viewModel, directory) = Create(OverSegmenting);
        var broken = Path.Combine(directory, "broken.wav");
        File.WriteAllText(broken, "this is not a wave file at all");

        viewModel.AddFiles([broken]);

        Assert.Single(viewModel.Jobs);
        Assert.Null(viewModel.Jobs[0].Duration);
        Assert.Null(viewModel.SpeakerDurationWarning);
    }

    [Fact]
    public async Task ACountAboveTheCapIsReportedAsUnreachableRatherThanApplied()
    {
        var (viewModel, directory) = Create(OverSegmenting);
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectTextAndTurns(viewModel);
        viewModel.LabelSpeakers = true;
        viewModel.SpeakerCount = 7;

        // Said before the run, because afterwards the only sentence left is "4 speakers were
        // labelled", which reads as a fact about the recording rather than about the tool.
        Assert.Contains("7 was never reachable", viewModel.SpeakerCountHint, StringComparison.Ordinal);

        // And it warns rather than refuses: the words are unaffected, only the labels are capped.
        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        viewModel.SelectedJob = viewModel.Jobs[0];
        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        Assert.Equal(4, SpeakersIn(Path.Combine(directory, "a.rttm")));
        Assert.Contains("is the most this labeller can tell apart", viewModel.Jobs[0].Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PastTheBoundTheBatchWarnsBesideTheQueueAndStillRuns()
    {
        // **This stopped the batch until 2026-08-28.** Past the bound is where a labeller that
        // declares one is measured to go wrong, and that is worth a sentence in front of the person
        // who can still act on it — but it is not grounds to refuse, because the number they would
        // type never reaches the clustering. It forces a fold instead, and a fold below the true
        // count merges two real people. So the warning is drawn beside the queue and Start stays
        // theirs to press.
        var (viewModel, directory) = Create(OverSegmenting with { ReliableUpTo = TimeSpan.FromSeconds(4) });
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectTextAndTurns(viewModel);
        viewModel.LabelSpeakers = true;

        Assert.Contains("a.wav", viewModel.SpeakerDurationWarning, StringComparison.Ordinal);
        Assert.Contains("only reliable", viewModel.SpeakerDurationWarning, StringComparison.Ordinal);

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.DoesNotContain("needs to know how many", viewModel.StatusMessage, StringComparison.Ordinal);

        // And the hint beside the field says what blank does rather than refusing it.
        Assert.Contains("Leave this blank", viewModel.SpeakerCountHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivingTheCountLetsThePastTheBoundBatchRun()
    {
        var (viewModel, directory) = Create(OverSegmenting with { ReliableUpTo = TimeSpan.FromSeconds(4) });
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectTextAndTurns(viewModel);
        viewModel.LabelSpeakers = true;
        viewModel.SpeakerCount = 2;

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        viewModel.SelectedJob = viewModel.Jobs[0];
        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        Assert.Equal(2, SpeakersIn(Path.Combine(directory, "a.rttm")));
    }

    [Fact]
    public async Task InsideTheBoundABlankCountRuns()
    {
        // A bound is still a fact worth saying — a labeller that declares one gets the warning
        // Inside the bound there is nothing to warn about and, since 2026-08-28, nothing to refuse
        // either. The guard that stood here was about the opt-in rather than the bound, so a short
        // queue was stopped by a rule whose evidence was a length none of its files reached.
        // Past the bound is PastTheBoundTheBatchWarnsBesideTheQueueAndStillRuns' claim.
        var (viewModel, directory) = Create(OverSegmenting with { ReliableUpTo = TimeSpan.FromMinutes(30) });
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectTextAndTurns(viewModel);
        viewModel.LabelSpeakers = true;

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.DoesNotContain("needs to know how many", viewModel.StatusMessage, StringComparison.Ordinal);

        // The model's own estimate, unfolded, because nothing asked for fewer. That a number still
        // folds is ACountTheLabellerCannotBeToldIsFoldedDownAfterwards' claim, not this one's —
        // asserting it here would need a second run, and a finished job does not run again.
        viewModel.SelectedJob = viewModel.Jobs[0];
        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        Assert.Equal(4, SpeakersIn(Path.Combine(directory, "a.rttm")));

        // Nothing queued is anywhere near thirty minutes, so there was never a warning here either.
        Assert.Null(viewModel.SpeakerDurationWarning);
    }

    [Fact]
    public async Task ZeroSpeakersIsRefusedWithAReasonRatherThanThrownMidBatch()
    {
        var (viewModel, directory) = Create(OverSegmenting);
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectTextAndTurns(viewModel);
        viewModel.LabelSpeakers = true;
        viewModel.SpeakerCount = 0;

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Pending, viewModel.Jobs[0].State);
        Assert.Contains("starts at one", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLongRecordingWarningTravelsWithTheFinishedTranscriptToo()
    {
        // On screen before the batch is where it can change a decision; on the row is where somebody
        // reads it a week later, beside the transcript it applies to. Both, for different readers.
        var (viewModel, directory) = Create(OverSegmenting with { ReliableUpTo = TimeSpan.FromSeconds(4) });
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectTextAndTurns(viewModel);
        viewModel.LabelSpeakers = true;
        viewModel.SpeakerCount = 2;   // past the bound the batch will not start without one

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.Contains(
            "treat the names as a guess",
            viewModel.Jobs[0].Warning,
            StringComparison.Ordinal);
    }
}
