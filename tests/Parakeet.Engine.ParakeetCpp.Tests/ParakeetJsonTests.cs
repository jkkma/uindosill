using System.Text.Json;
using Parakeet.Engine.ParakeetCpp;

namespace Parakeet.Engine.ParakeetCpp.Tests;

public class ParakeetJsonTests
{
    // The shape documented in include/parakeet_capi.h at ABI v6.
    private const string OneClip = """
        {"text":"hello there",
         "frame_sec":0.080000,
         "words":[{"w":"hello","start":0.480,"end":0.640,"conf":0.9100},
                  {"w":"there","start":0.720,"end":0.960,"conf":0.8800}],
         "tokens":[{"id":123,"t":0.480,"conf":0.9100}]}
        """;

    [Fact]
    public void BatchArrayIsParsedInOrder()
    {
        var json = $"[{OneClip},{OneClip}]";
        var clips = ParakeetJson.ParseBatch(json);

        Assert.Equal(2, clips.Count);
        Assert.Equal("hello there", clips[0].Text);
        Assert.Equal(0.08, clips[0].FrameSeconds!.Value, 6);
    }

    [Fact]
    public void WordTimingsAndConfidenceSurvive()
    {
        var clip = ParakeetJson.ParseBatch($"[{OneClip}]")[0];

        Assert.Equal(2, clip.Words.Count);
        Assert.Equal("hello", clip.Words[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(0.48), clip.Words[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(0.64), clip.Words[0].End);
        Assert.Equal(0.91f, clip.Words[0].Confidence!.Value, 4);
    }

    [Fact]
    public void WordTimesLandOnTheTickTheySayRatherThanOneUnder()
    {
        // 0.57 through TimeSpan.FromSeconds is 5,699,999 ticks, which a subtitle prints as
        // 00:00:00,569 while the JSON beside it says 0.57 (GOTCHAS §25). Rounded now, as the RTTM
        // reader's times always were.
        var clip = ParakeetJson.ParseBatch("""[{"text":"x","words":[{"w":"x","start":0.57,"end":1.23}]}]""")[0];

        Assert.Equal(5_700_000, clip.Words[0].Start.Ticks);
        Assert.Equal(12_300_000, clip.Words[0].End.Ticks);
    }

    [Fact]
    public void FrameSecondsComesFromTheEngineRatherThanBeingDerived()
    {
        // The engine supplies hop_length * subsampling_factor / sample_rate, so nothing here
        // has to guess a subsampling factor — the whole reason to read this field.
        var clip = ParakeetJson.ParseBatch($"[{OneClip}]")[0];
        Assert.Equal(0.08, clip.FrameSeconds!.Value, 6);
    }

    [Fact]
    public void SingleObjectIsAcceptedWhereABatchWasExpected()
    {
        var clips = ParakeetJson.ParseBatch(OneClip);
        Assert.Single(clips);
    }

    [Fact]
    public void MissingWordsArrayIsNotAFailure()
    {
        var clips = ParakeetJson.ParseBatch("""[{"text":"just text"}]""");

        Assert.Equal("just text", clips[0].Text);
        Assert.Empty(clips[0].Words);
        Assert.Null(clips[0].FrameSeconds);
    }

    [Fact]
    public void EmptyTranscriptIsRepresentedFaithfully()
    {
        var clips = ParakeetJson.ParseBatch("""[{"text":"","frame_sec":0.08,"words":[]}]""");

        Assert.Equal(string.Empty, clips[0].Text);
        Assert.Empty(clips[0].Words);
    }

    [Fact]
    public void InvertedWordTimesAreCollapsedRatherThanPassedOn()
    {
        // An end before its start becomes a subtitle cue players drop silently.
        var clips = ParakeetJson.ParseBatch("""[{"text":"x","words":[{"w":"x","start":2.0,"end":1.0}]}]""");

        Assert.Equal(clips[0].Words[0].Start, clips[0].Words[0].End);
    }

    [Fact]
    public void NegativeTimesAreClampedToZero()
    {
        var clips = ParakeetJson.ParseBatch("""[{"text":"x","words":[{"w":"x","start":-5,"end":1.0}]}]""");

        Assert.Equal(TimeSpan.Zero, clips[0].Words[0].Start);
    }

    [Fact]
    public void WordsWithoutTextAreDropped()
    {
        var clips = ParakeetJson.ParseBatch("""[{"text":"x","words":[{"start":0,"end":1},{"w":"kept","start":1,"end":2}]}]""");

        var word = Assert.Single(clips[0].Words);
        Assert.Equal("kept", word.Text);
    }

    [Fact]
    public void UnicodeEscapesAreDecoded()
    {
        var clips = ParakeetJson.ParseBatch("""[{"text":"café naïve"}]""");
        Assert.Equal("café naïve", clips[0].Text);
    }

    [Fact]
    public void OutputThatIsNotJsonIsReportedWithAPreview()
    {
        var exception = Assert.Throws<ParakeetNativeException>(
            () => ParakeetJson.ParseBatch("terminal output, not a document"));

        Assert.Contains("not JSON", exception.Message, StringComparison.Ordinal);
        Assert.Contains("terminal output", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
    }

    [Fact]
    public void UnexpectedRootKindIsRejected()
    {
        var exception = Assert.Throws<ParakeetNativeException>(() => ParakeetJson.ParseBatch("\"a string\""));
        Assert.Contains("Expected a JSON array", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyStringIsRejected() =>
        Assert.Throws<ArgumentException>(() => ParakeetJson.ParseBatch(string.Empty));
}
