namespace Parakeet.Engine.Sortformer.Tests;

/// <summary>
/// The streaming loop's slicing arithmetic, against the reference driver's.
/// </summary>
/// <remarks>
/// No model and no tensors are involved — it is pure index arithmetic — but it is arithmetic whose
/// mistakes are all silent. Feed the graph a chunk one frame short of its left context and the
/// speaker cache admits a frame that belongs to the previous chunk; round the right context down
/// instead of up and the last chunk of every recording reports on lookahead it should have dropped.
/// Neither throws, and both cost DER.
/// </remarks>
public class ChunkPlanTests
{
    public static TheoryData<int> Lengths()
    {
        var data = new TheoryData<int>();
        using var manifest = Fixtures.Manifest();
        foreach (var entry in manifest.RootElement.GetProperty("chunkPlan").GetProperty("cases").EnumerateArray())
        {
            data.Add(entry.GetProperty("seconds").GetInt32());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void ThePlanMatchesTheReferenceDriver(int seconds)
    {
        using var manifest = Fixtures.Manifest();
        var expected = manifest.RootElement.GetProperty("chunkPlan").GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("seconds").GetInt32() == seconds);

        var padded = expected.GetProperty("paddedFrames").GetInt32();
        var valid = expected.GetProperty("validFrames").GetInt32();

        var start = 0;
        var index = 0;
        foreach (var step in expected.GetProperty("steps").EnumerateArray())
        {
            var actual = SortformerChunkPlan.Next(start, padded, valid);

            Assert.Equal(step.GetProperty("melStart").GetInt32(), actual.MelStart);
            Assert.Equal(step.GetProperty("melWidth").GetInt32(), actual.MelWidth);
            Assert.Equal(step.GetProperty("chunkLengthFrames").GetInt32(), actual.ChunkLengthFrames);
            Assert.Equal(step.GetProperty("leftContextEncoderFrames").GetInt32(), actual.LeftContextEncoderFrames);
            Assert.Equal(step.GetProperty("rightContextEncoderFrames").GetInt32(), actual.RightContextEncoderFrames);

            start = actual.End;
            index++;
        }

        Assert.True(start >= padded, $"the plan stopped at {start} of {padded} mel frames after {index} steps");
    }

    /// <summary>
    /// The same plan, arrived at without knowing the recording's length until the audio runs out —
    /// which is the situation the real loop is in, because it streams rather than loading the file.
    /// Every step but the last must be identical to the step a reader who knew the length would take.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lengths))]
    public void NotKnowingTheLengthInAdvanceChangesNothingButTheLastStep(int seconds)
    {
        using var manifest = Fixtures.Manifest();
        var expected = manifest.RootElement.GetProperty("chunkPlan").GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("seconds").GetInt32() == seconds);

        var padded = expected.GetProperty("paddedFrames").GetInt32();
        var valid = expected.GetProperty("validFrames").GetInt32();

        var start = 0;
        foreach (var step in expected.GetProperty("steps").EnumerateArray())
        {
            // A reader learns the totals exactly when a step asks for frames the recording does not
            // have — that is, when this step's widest possible request runs past the end.
            var leftOffset = Math.Min(
                SortformerGeometry.ChunkLeftContext * SortformerGeometry.SubsamplingFactor, start);
            var reachesTheEnd = start - leftOffset + SortformerChunkPlan.MaximumWidth > padded;

            var actual = reachesTheEnd
                ? SortformerChunkPlan.Next(start, padded, valid)
                : SortformerChunkPlan.Next(start, null, null);

            Assert.Equal(step.GetProperty("melStart").GetInt32(), actual.MelStart);
            Assert.Equal(step.GetProperty("melWidth").GetInt32(), actual.MelWidth);
            Assert.Equal(step.GetProperty("chunkLengthFrames").GetInt32(), actual.ChunkLengthFrames);
            Assert.Equal(step.GetProperty("leftContextEncoderFrames").GetInt32(), actual.LeftContextEncoderFrames);
            Assert.Equal(step.GetProperty("rightContextEncoderFrames").GetInt32(), actual.RightContextEncoderFrames);

            start = actual.End;
        }
    }

    /// <summary>
    /// The first chunk has no left context because there is no audio before it, and every later one
    /// has exactly one encoder frame of it. Asserted on its own because it is the asymmetry the
    /// reference's loop expresses as <c>min(8, stt)</c>, which reads like a clamp and is a special
    /// case.
    /// </summary>
    [Fact]
    public void OnlyTheFirstChunkHasNoLeftContext()
    {
        var first = SortformerChunkPlan.Next(0, null, null);
        Assert.Equal(0, first.LeftContextEncoderFrames);
        Assert.Equal(0, first.MelStart);

        var second = SortformerChunkPlan.Next(first.End, null, null);
        Assert.Equal(SortformerGeometry.ChunkLeftContext, second.LeftContextEncoderFrames);
        Assert.Equal(first.End - SortformerGeometry.SubsamplingFactor, second.MelStart);
    }
}
