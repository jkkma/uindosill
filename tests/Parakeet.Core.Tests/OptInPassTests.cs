using Parakeet.Core.Jobs;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

/// <summary>
/// The policy for an opt-in pass that fails after the transcript is finished: the transcript comes
/// back as it was, with the reason, and what is written is what was produced.
/// </summary>
/// <remarks>
/// Until 2026-08-22 neither surface had this, and a labeller or translator that threw after the
/// ASR pass failed the whole file — minutes of decode unwritten, and in a batch whose sidecar had
/// died, every remaining file decoded and then discarded the same way. The words were unaffected
/// throughout, which is what these tests hold the helper to.
/// </remarks>
public class OptInPassTests
{
    private static TranscriptDocument Document() => new()
    {
        Segments =
        [
            new TranscriptSegment { Text = "hola", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1) },
        ],
        SourceName = "a.wav",
    };

    [Fact]
    public async Task APassThatThrowsHandsBackTheTranscriptUnchangedAndSaysWhy()
    {
        var document = Document();

        var (result, failure) = await OptInPass.Speakers.RunAsync(
            document,
            () => throw new InvalidOperationException("the child died"));

        Assert.Same(document, result);
        Assert.NotNull(failure);
        Assert.Equal(OptInPass.Speakers, failure!.Pass);
        Assert.Equal("the child died", failure.Reason);
        Assert.IsType<InvalidOperationException>(failure.Exception);

        // The sentence both surfaces print: which pass, that the transcript went out without its
        // product, and the reason — in that order, because the reader's first question is "what do
        // I not have", not "what went wrong inside it".
        Assert.Equal(
            "Speaker labelling failed for this file, so the transcript was written without speaker labels: the child died",
            failure.Describe());
        Assert.StartsWith(
            "Translation failed for this file, so the transcript was written without the English version:",
            new PassFailure(OptInPass.Translation, "refused").Describe(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task APassThatSucceedsHandsBackWhatItProducedAndNoFailure()
    {
        var source = Document();
        var produced = source with { Segments = [source.Segments[0] with { Speaker = "Speaker 1" }] };

        var (result, failure) = await OptInPass.Speakers.RunAsync(source, () => Task.FromResult(produced));

        Assert.Same(produced, result);
        Assert.Null(failure);
    }

    [Fact]
    public async Task CancellationIsNotAFailureAndIsNotCaught()
    {
        // A cancelled batch is reported as cancelled by the runner, file by file. Turning the
        // cancellation into "written without speakers" would write a transcript the user had just
        // asked not to have.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OptInPass.Translation.RunAsync(
            Document(),
            async () =>
            {
                await Task.Yield();
                cancelled.Token.ThrowIfCancellationRequested();
                return Document();
            }));
    }

    [Fact]
    public void AJobWithoutItsTranslationLosesTheEnglishNameAndAJobWithoutItsSpeakersLosesTheTurnsFormat()
    {
        // The .en infix promises English and an .rttm promises turns; a transcript written without
        // the pass that would have produced either cannot carry the name. The speaker rule goes
        // through the format registry so an alias spelling — ".rttm", "RTTM" — is dropped too.
        var job = new TranscriptionJob
        {
            InputPath = "call.wav",
            Formats = ["txt", "RTTM", ".rttm", "srt"],
            StemSuffix = ".en",
        };

        var untranslated = job.WithoutFailedPasses([new PassFailure(OptInPass.Translation, "refused")]);
        Assert.Equal(string.Empty, untranslated.StemSuffix);
        Assert.Equal(job.Formats, untranslated.Formats);

        var unlabelled = job.WithoutFailedPasses([new PassFailure(OptInPass.Speakers, "died")]);
        Assert.Equal(".en", unlabelled.StemSuffix);
        Assert.Equal(["txt", "srt"], unlabelled.Formats);

        var both = job.WithoutFailedPasses(
            [new PassFailure(OptInPass.Speakers, "died"), new PassFailure(OptInPass.Translation, "refused")]);
        Assert.Equal(string.Empty, both.StemSuffix);
        Assert.Equal(["txt", "srt"], both.Formats);

        // Nothing failed, nothing changes — including the instance, so a caller can compare.
        Assert.Same(job, job.WithoutFailedPasses([]));
    }
}
