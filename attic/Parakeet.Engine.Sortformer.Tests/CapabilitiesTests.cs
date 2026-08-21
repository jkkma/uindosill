using Parakeet.Core.Diarisation;

namespace Parakeet.Engine.Sortformer.Tests;

/// <summary>
/// What the loaded labeller declares about its own limits, which is what every caller branches on.
/// </summary>
/// <remarks>
/// Constructing touches no disk, so this needs no weights and runs in CI. There are two limits and
/// they are different kinds of thing: <see cref="SpeakerLabellerCapabilities.MaxSpeakers"/> is
/// architectural — four is in the model's geometry, the same on every file, knowable without
/// running anything — and <see cref="SpeakerLabellerCapabilities.ReliableUpTo"/> is empirical: it
/// is where the scoring stopped. A caller that reported one believing it had reported the other
/// would tell somebody with a three-hour two-host recording about a cap that is not their problem.
/// </remarks>
public class CapabilitiesTests
{
    private static SpeakerLabellerCapabilities Declared() =>
        new SortformerSpeakerLabeller(new SortformerLabellerOptions
        {
            ModelPath = "nowhere-in-particular",
            ModelId = "sortformer-4spk-v2.1",
        }).Capabilities;

    [Fact]
    public void BothLimitsAreDeclaredAndTheyAreNotTheSameLimit()
    {
        var capabilities = Declared();

        Assert.Equal(SortformerGeometry.SpeakerCount, capabilities.MaxSpeakers);
        Assert.Equal(4, capabilities.MaxSpeakers);

        // Fifty minutes, measured 2026-08-20 by growing the window from a fixed onset: it is the
        // longest length at which every window tested came out right, not the shortest at which one
        // came out wrong. An hour would have let the one failing sixty-minute window through. If
        // this number changes, the measurement behind it has to change too: docs/UNPROVEN.md
        // carries the ladder it came from.
        Assert.Equal(TimeSpan.FromMinutes(50), capabilities.ReliableUpTo);

        // The model estimates the count and cannot be told one. That is what makes --speaker-count
        // report itself ignored rather than appear to work.
        Assert.False(capabilities.SupportsFixedSpeakerCount);
    }

    [Fact]
    public void AThreeHourRecordingIsWarnedAboutEvenWhenNobodyAskedForACount()
    {
        // The case this exists for, and the one the cap warning does not cover: two hosts, no
        // --speaker-count, and a recording four times longer than anything the gate scored.
        var capabilities = Declared();

        Assert.Null(SpeakerLabelling.DescribeUnreachableCount(capabilities, requested: null));
        Assert.NotNull(SpeakerLabelling.DescribeDurationRisk(capabilities, TimeSpan.FromHours(3)));

        // And an AMI-length meeting, which is what the gate was passed on, is not warned about.
        Assert.Null(SpeakerLabelling.DescribeDurationRisk(capabilities, TimeSpan.FromMinutes(32)));

        // The sixty-minute case is the one the bound exists to catch: one of the four windows tested
        // at that length was wrong, so it is inside the warning rather than outside it.
        Assert.NotNull(SpeakerLabelling.DescribeDurationRisk(capabilities, TimeSpan.FromMinutes(60)));
    }
}
