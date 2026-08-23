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
        var directory = Directory.CreateTempSubdirectory("uindosill-spk").FullName;
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
        // speakers on one of the eighteen AMI development meetings. But blank no longer runs — the
        // hint says the field is required the moment the opt-in makes it live.
        Assert.Null(viewModel.SpeakerCount);
        Assert.False(viewModel.CanSetSpeakerCount);
        Assert.Null(viewModel.SpeakerCountHint);

        viewModel.LabelSpeakers = true;
        Assert.True(viewModel.CanSetSpeakerCount);
        Assert.Contains("does not run without it", viewModel.SpeakerCountHint, StringComparison.Ordinal);

        // And it says the count will be honoured afterwards rather than by the model, because those
        // are different facts and only one of them is what happens.
        viewModel.SpeakerCount = 2;
        Assert.Contains("folded down to 2 afterwards", viewModel.SpeakerCountHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutACountTheBatchRefusesToStart()
    {
        // The estimate this refusal replaces invents four voices on a drifted recording, the
        // transcript gets four names, and no sentence anywhere says which of "four people" and
        // "one person heard twice" happened. So the window does not take a blank at all: the count
        // is required the moment the opt-in is on — no bound involved, this labeller has none set.
        var (viewModel, directory) = Create(OverSegmenting);
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectTextAndTurns(viewModel);
        viewModel.LabelSpeakers = true;

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Pending, viewModel.Jobs[0].State);
        Assert.Contains("needs to know how many", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Set 'How many speakers'", viewModel.StatusMessage, StringComparison.Ordinal);
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
        // anything: it produced four and the repair ran on its output.
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
        Assert.Contains("not known to be wrong so much as not known to be right", warning, StringComparison.Ordinal);
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
        Assert.Equal(4, SpeakersIn(Path.Combine(directory, "a.rttm")));
        Assert.Contains("is the most this labeller can tell apart", viewModel.Jobs[0].Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PastTheBoundTheBatchStopsAndAsksForACount()
    {
        // Blank refuses everywhere now, but past the bound the refusal earns a sharper sentence:
        // this is the recording where estimating is measured to go wrong — one host over-segmented
        // into two labels, silently, on a file somebody is about to spend half an hour on — so the
        // message names the file rather than reciting the rule.
        var (viewModel, directory) = Create(OverSegmenting with { ReliableUpTo = TimeSpan.FromSeconds(4) });
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectTextAndTurns(viewModel);
        viewModel.LabelSpeakers = true;

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Pending, viewModel.Jobs[0].State);
        Assert.Contains("a.wav is longer than", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Set 'How many speakers'", viewModel.StatusMessage, StringComparison.Ordinal);

        // The hint beside the field says the same thing at the same moment, rather than letting
        // Start be the first place anybody hears it.
        Assert.Contains("does not run without it", viewModel.SpeakerCountHint, StringComparison.Ordinal);

        // Two ways out, and both are decisions rather than guesses. Turning the opt-in off runs —
        // dropping RTTM with it, since speaker turns are that opt-in's output and the window already
        // refuses to write an empty one.
        viewModel.LabelSpeakers = false;
        viewModel.Formats.First(f => f.Id == "rttm").IsSelected = false;

        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
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
        Assert.Equal(2, SpeakersIn(Path.Combine(directory, "a.rttm")));
    }

    [Fact]
    public async Task InsideTheBoundABlankCountIsStillRefused()
    {
        // The guard is about the opt-in, not the bound. Inside the bound the estimate is measured
        // correct, and the count is required anyway, because a wrong estimate is silent wherever it
        // happens and the person pressing Start knows the number. What the bound still changes is
        // the sentence: a short queue gets the rule, not a file named as past the evidence.
        var (viewModel, directory) = Create(OverSegmenting with { ReliableUpTo = TimeSpan.FromMinutes(30) });
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectTextAndTurns(viewModel);
        viewModel.LabelSpeakers = true;

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Pending, viewModel.Jobs[0].State);
        Assert.Contains("needs to know how many", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("is longer than", viewModel.StatusMessage, StringComparison.Ordinal);

        // Giving the number is the way through, exactly as it is past the bound.
        viewModel.SpeakerCount = 2;
        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.Equal(2, SpeakersIn(Path.Combine(directory, "a.rttm")));
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
            "not known to be wrong so much as not known to be right",
            viewModel.Jobs[0].Warning,
            StringComparison.Ordinal);
    }
}
