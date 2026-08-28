using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

/// <summary>
/// The third evidence tier: a question about the whole recording, answered from an even sample of
/// all of it when reading every minute will not fit.
/// </summary>
public class SurveyWindowSelectorTests
{
    /// <summary>Cover windows over a recording of <paramref name="minutes"/> one-minute segments,
    /// each window's text a fixed <paramref name="chars"/> long so a budget is arithmetic.</summary>
    private static IReadOnlyList<TranscriptWindow> Cover(int minutes, int chars = 1_000)
    {
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < minutes; i++)
        {
            segments.Add(new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(i * 60),
                End = TimeSpan.FromSeconds((i * 60) + 60),
                Text = new string('x', chars),
            });
        }

        var document = new TranscriptDocument
        {
            Segments = segments,
            AudioDuration = TimeSpan.FromSeconds(minutes * 60),
        };

        return TranscriptWindowBuilder.Build(document, TranscriptWindowOptions.Cover);
    }

    [Fact]
    public void ARecordingThatAlreadyFitsIsNotSampledAtAll()
    {
        // The caller would have used the whole-transcript path; returning a subset here would
        // drop material for no reason.
        var cover = Cover(10);

        Assert.Same(cover, SurveyWindowSelector.Select(cover, 1_000_000));
    }

    [Fact]
    public void TheSampleFitsTheBudgetAndReachesBothEnds()
    {
        // A three-hour recording against the panel's own budget: the answer must cover the
        // recording, and an answer drawn from the opening minutes is the exact failure the
        // whole-recording instruction exists to steer away from — so a survey that sampled only
        // the start would look like a fix while reintroducing the bug.
        var cover = Cover(180);
        var picked = SurveyWindowSelector.Select(cover, 32_000);

        Assert.True(picked.Count > 1);
        Assert.True(picked.Sum(w => w.Text.Length) <= 32_000);
        Assert.Equal(cover[0].FirstSegment, picked[0].FirstSegment);
        Assert.Equal(cover[^1].LastSegment, picked[^1].LastSegment);
    }

    [Fact]
    public void TheSampleIsEvenlySpreadRatherThanContiguous()
    {
        // Coverage is the whole point. A contiguous prefix that happened to fit would be the
        // opening minutes again, and a scored subset would be the retrieval fallback this tier
        // replaces — a global question is where a scorer has least to rank on.
        var cover = Cover(180);
        var picked = SurveyWindowSelector.Select(cover, 32_000);

        var gaps = picked.Zip(picked.Skip(1), (a, b) => b.FirstSegment - a.FirstSegment).ToList();
        Assert.All(gaps, gap => Assert.True(gap > 0, "the sample runs forward through the recording"));

        // Evenly spread means the gaps agree with each other; rounding lets them differ by one.
        Assert.True(gaps.Max() - gaps.Min() <= 1, $"gaps ranged {gaps.Min()}..{gaps.Max()}");
    }

    [Fact]
    public void ABudgetTooSmallForOneWindowStillReturnsEvidence()
    {
        // Returning nothing would make the engine abstain, and an abstention is a claim about the
        // recording — "it does not answer that" — which would be false. One window is a poor
        // survey and an honest one.
        var cover = Cover(180);

        var picked = SurveyWindowSelector.Select(cover, 1);

        Assert.Single(picked);
        Assert.Equal(cover[0].FirstSegment, picked[0].FirstSegment);
    }

    [Fact]
    public void NothingInIsNothingOut()
    {
        Assert.Empty(SurveyWindowSelector.Select([], 32_000));
    }

    [Fact]
    public void UnevenWindowsAreMeasuredRatherThanAssumed()
    {
        // Windows are not the same size in a real recording — a minute of rapid exchange carries
        // far more text than a minute of silence — so the count that fits is searched for against
        // the actual text rather than divided out of an average.
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < 60; i++)
        {
            segments.Add(new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(i * 60),
                End = TimeSpan.FromSeconds((i * 60) + 60),
                Text = new string('x', i % 2 == 0 ? 200 : 4_000),
            });
        }

        var cover = TranscriptWindowBuilder.Build(
            new TranscriptDocument { Segments = segments, AudioDuration = TimeSpan.FromSeconds(3_600) },
            TranscriptWindowOptions.Cover);

        var picked = SurveyWindowSelector.Select(cover, 20_000);

        Assert.NotEmpty(picked);
        Assert.True(
            picked.Sum(w => w.Text.Length) <= 20_000,
            $"picked {picked.Sum(w => w.Text.Length)} chars against a 20,000 budget");
    }
}
