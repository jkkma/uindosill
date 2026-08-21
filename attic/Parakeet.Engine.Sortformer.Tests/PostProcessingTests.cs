using System.Globalization;

namespace Parakeet.Engine.Sortformer.Tests;

/// <summary>
/// The post-processing against NeMo's <c>ts_vad_post_processing</c>, on seven parameter sets.
/// </summary>
/// <remarks>
/// Seven rather than one, because the set that produced the passing DER has onset equal to offset —
/// which degenerates the hysteresis into a plain comparison, so a port with the two thresholds
/// swapped would pass a test written only against it. The other six separate the thresholds and move
/// each filter in turn, including the order the two filters run in, which NeMo's own YAML comments
/// have the wrong way round.
/// </remarks>
public class PostProcessingTests
{
    public static TheoryData<int> Sets()
    {
        var data = new TheoryData<int>();
        using var manifest = Fixtures.Manifest();
        var count = manifest.RootElement.GetProperty("postProcessing").GetProperty("sets").GetArrayLength();
        for (var i = 0; i < count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void TheSegmentsReproduceWhatNeMosPostProcessingProduced(int set)
    {
        using var manifest = Fixtures.Manifest();
        var fixture = manifest.RootElement.GetProperty("postProcessing");
        var frames = fixture.GetProperty("frames").GetInt32();
        var speakers = fixture.GetProperty("speakers").GetInt32();
        var entry = fixture.GetProperty("sets")[set];
        var parameters = entry.GetProperty("parameters");

        var options = new SortformerPostProcessingOptions
        {
            Onset = parameters.GetProperty("onset").GetDouble(),
            Offset = parameters.GetProperty("offset").GetDouble(),
            PadOnset = TimeSpan.FromSeconds(parameters.GetProperty("pad_onset").GetDouble()),
            PadOffset = TimeSpan.FromSeconds(parameters.GetProperty("pad_offset").GetDouble()),
            MinimumSpeechDuration = TimeSpan.FromSeconds(parameters.GetProperty("min_on").GetDouble()),
            MinimumSilenceDuration = TimeSpan.FromSeconds(parameters.GetProperty("min_off").GetDouble()),
        };

        var probabilities = DeterministicInputs.Probabilities(frames, speakers);
        var turns = SortformerPostProcessing.ToTurns(probabilities, speakers, options);

        var expected = entry.GetProperty("segments");
        Assert.Equal(expected.GetArrayLength(), turns.Count);

        var index = 0;
        foreach (var segment in expected.EnumerateArray())
        {
            var turn = turns[index];
            var speaker = segment.GetProperty("speaker").GetInt32();

            Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"spk{speaker}"), turn.Speaker);

            // A tick either way: the reference works in float seconds and this works in ticks, and
            // the rounding between them is the only difference allowed here.
            Assert.InRange(
                Math.Abs(turn.Start.TotalSeconds - segment.GetProperty("start").GetDouble()),
                0.0,
                1e-6);
            Assert.InRange(
                Math.Abs(turn.End.TotalSeconds - segment.GetProperty("end").GetDouble()),
                0.0,
                1e-6);

            index++;
        }
    }

    /// <summary>
    /// The hysteresis proper, on a signal built to need it: a rise that crosses onset, a dip between
    /// the two thresholds that must not close the segment, and a fall below offset that must.
    /// </summary>
    [Fact]
    public void AProbabilityBetweenTheThresholdsLeavesTheStateAlone()
    {
        float[] track = [0.1f, 0.9f, 0.5f, 0.9f, 0.1f, 0.1f];
        var turns = SortformerPostProcessing.ToTurns(
            track,
            speakers: 1,
            new SortformerPostProcessingOptions
            {
                Onset = 0.8,
                Offset = 0.2,
                PadOnset = TimeSpan.Zero,
                PadOffset = TimeSpan.Zero,
                MinimumSpeechDuration = TimeSpan.Zero,
                MinimumSilenceDuration = TimeSpan.Zero,
            });

        var turn = Assert.Single(turns);
        Assert.Equal(1 * SortformerGeometry.FrameSeconds, turn.Start.TotalSeconds, 6);
        Assert.Equal(4 * SortformerGeometry.FrameSeconds, turn.End.TotalSeconds, 6);
    }

    /// <summary>
    /// Speech still going at the last frame is closed at the end of the recording rather than
    /// dropped, which is the one case the vectorised reference handles with a special branch.
    /// </summary>
    [Fact]
    public void SpeechRunningToTheEndIsClosedAtTheEnd()
    {
        float[] track = [0.1f, 0.9f, 0.9f];
        var turns = SortformerPostProcessing.ToTurns(
            track,
            speakers: 1,
            new SortformerPostProcessingOptions
            {
                Onset = 0.5,
                Offset = 0.5,
                PadOnset = TimeSpan.Zero,
                PadOffset = TimeSpan.Zero,
                MinimumSpeechDuration = TimeSpan.Zero,
                MinimumSilenceDuration = TimeSpan.Zero,
            });

        var turn = Assert.Single(turns);
        Assert.Equal(3 * SortformerGeometry.FrameSeconds, turn.End.TotalSeconds, 6);
    }

    /// <summary>
    /// Short speech is deleted before short gaps are filled, not after. The other order lets a
    /// gap-fill rescue a segment that should have gone, and this is the case that separates them:
    /// two 80 ms bursts 240 ms apart, with a filter that would delete each and a gap-fill that would
    /// have joined them into one long enough to survive.
    /// </summary>
    [Fact]
    public void ShortSpeechIsDroppedBeforeShortGapsAreFilled()
    {
        float[] track = [0.9f, 0.1f, 0.1f, 0.1f, 0.9f, 0.1f];
        var turns = SortformerPostProcessing.ToTurns(
            track,
            speakers: 1,
            new SortformerPostProcessingOptions
            {
                Onset = 0.5,
                Offset = 0.5,
                PadOnset = TimeSpan.Zero,
                PadOffset = TimeSpan.Zero,
                MinimumSpeechDuration = TimeSpan.FromSeconds(0.16),
                MinimumSilenceDuration = TimeSpan.FromSeconds(0.4),
            });

        Assert.Empty(turns);
    }

    /// <summary>
    /// The thresholds have to be this way round, and transposing them is a one-character mistake
    /// between two adjacent properties of the same type.
    /// </summary>
    /// <remarks>
    /// Reversed, "above onset" and "below offset" are both true for every frame between them, so
    /// the state machine opens on one frame and closes on the next for as long as the probability
    /// stays in the band — a checkerboard of 80 ms segments rather than anything that looks wrong.
    /// It also invalidates the binariser's own correctness argument, which turns on offset <= onset.
    /// </remarks>
    [Fact]
    public void AnOffsetAboveTheOnsetIsRefusedRatherThanOscillating()
    {
        var reversed = new SortformerPostProcessingOptions { Onset = 0.2, Offset = 0.8 };

        var error = Assert.Throws<ArgumentOutOfRangeException>(reversed.Validate);
        Assert.Contains("must not be above the onset", error.Message, StringComparison.Ordinal);

        // Equal is the tuned set and must stay legal.
        new SortformerPostProcessingOptions { Onset = 0.5, Offset = 0.5 }.Validate();
        new SortformerPostProcessingOptions { Onset = 0.8, Offset = 0.2 }.Validate();
    }

    /// <summary>The tuned defaults, spelled out: changing one invalidates the measured DER.</summary>
    [Fact]
    public void TheDefaultsAreTheParametersTheGateWasPassedWith()
    {
        var options = SortformerPostProcessingOptions.Default;
        Assert.Equal(0.5, options.Onset);
        Assert.Equal(0.5, options.Offset);
        Assert.Equal(TimeSpan.FromMilliseconds(50), options.PadOnset);
        Assert.Equal(TimeSpan.Zero, options.PadOffset);
        Assert.Equal(TimeSpan.Zero, options.MinimumSpeechDuration);
        Assert.Equal(TimeSpan.FromSeconds(1), options.MinimumSilenceDuration);
    }
}
