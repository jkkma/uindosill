using System.Text.Json;

namespace Parakeet.Engine.Sortformer.Tests;

/// <summary>
/// The Arrival-Order Speaker Cache against NVIDIA's own <c>streaming_update_async</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ten steps of the reference function, run at the real geometry and recorded tensor by tensor, are
/// replayed through the port. This is the whole of the correctness claim for the riskiest code in
/// the diariser: the spike deliberately did not port this — it imported NVIDIA's function and called
/// it — so unlike everything else here it is not a translation of something already known to work.
/// </para>
/// <para>
/// <b>Why the fixture is eight-dimensional.</b> The algorithm never does arithmetic across the
/// embedding dimension except one masked mean, so every index computation, score, boost, top-k and
/// eviction it performs is identical at 8 as at 512, and the whole oracle fits in half a megabyte
/// instead of fifty. The embeddings carry <c>frame + dimension/16</c> rather than noise, so a gather
/// that reads the wrong frame, or reads down the wrong stride, is visible in the value itself rather
/// than only in a statistic.
/// </para>
/// </remarks>
public class SpeakerCacheTests
{
    private sealed record Step(int Index, int LeftContext, int RightContext, int PhysicalFrames, int MaxChunkLength);

    private static (JsonElement Cache, float[] Blob) Load()
    {
        using var manifest = Fixtures.Manifest();
        return (manifest.RootElement.GetProperty("speakerCache").Clone(), Fixtures.ReadFloats("speaker-cache.f32"));
    }

    private static ReadOnlySpan<float> Tensor(float[] blob, JsonElement step, string name)
    {
        var entry = step.GetProperty("tensors").GetProperty(name);
        var offset = entry.GetProperty("offset").GetInt32();
        var length = 1;
        foreach (var dimension in entry.GetProperty("shape").EnumerateArray())
        {
            length *= dimension.GetInt32();
        }

        return blob.AsSpan(offset, length);
    }

    /// <summary>
    /// Every step replayed in order, with the state checked after each. Not just the last: a cache
    /// that drifts in step three and is overwritten in step four would pass an end-state assertion.
    /// </summary>
    [Fact]
    public void TheCacheReproducesNvidiasStreamingUpdate()
    {
        var (fixture, blob) = Load();
        var dimension = fixture.GetProperty("embeddingDimension").GetInt32();
        var cache = new ArrivalOrderSpeakerCache(dimension);
        var chunkPredictions = new float[SortformerGeometry.ChunkLength * SortformerGeometry.SpeakerCount];

        foreach (var step in fixture.GetProperty("steps").EnumerateArray())
        {
            var index = step.GetProperty("step").GetInt32();
            var before = step.GetProperty("before");
            var after = step.GetProperty("after");

            // The state the port carries in must already be the state the reference carried in,
            // or the step is being checked against the wrong starting point.
            Assert.Equal(before.GetProperty("spkcacheLength").GetInt32(), cache.CacheFrames);
            Assert.Equal(before.GetProperty("fifoLength").GetInt32(), cache.FifoFrames);
            Assert.Equal(before.GetProperty("spkcacheCompressed").GetBoolean(), cache.HasCompressed);
            Assert.Equal(before.GetProperty("silenceFrames").GetInt64(), cache.SilenceFrames);
            Deviation.Within(cache.Cache, Tensor(blob, step, "inSpkcache"), 1e-3, $"step {index} incoming cache");
            Deviation.Within(cache.Fifo, Tensor(blob, step, "inFifo"), 1e-3, $"step {index} incoming FIFO");
            Deviation.Within(cache.MeanSilence, Tensor(blob, step, "inMeanSilence"), 1e-3, $"step {index} incoming silence profile");

            var written = cache.Update(
                Tensor(blob, step, "chunk"),
                step.GetProperty("physicalChunkFrames").GetInt32(),
                Tensor(blob, step, "preds"),
                step.GetProperty("leftContext").GetInt32(),
                step.GetProperty("rightContext").GetInt32(),
                chunkPredictions);

            Assert.Equal(step.GetProperty("maxChunkLength").GetInt32(), written);

            Assert.Equal(after.GetProperty("spkcacheLength").GetInt32(), cache.CacheFrames);
            Assert.Equal(after.GetProperty("fifoLength").GetInt32(), cache.FifoFrames);
            Assert.Equal(after.GetProperty("spkcacheCompressed").GetBoolean(), cache.HasCompressed);
            Assert.Equal(after.GetProperty("silenceFrames").GetInt64(), cache.SilenceFrames);

            Deviation.Within(
                chunkPredictions.AsSpan(0, written * SortformerGeometry.SpeakerCount),
                Tensor(blob, step, "outChunkPreds"),
                0.0,
                $"step {index} chunk predictions");

            // The embeddings are gathered, never arithmetic, so these must agree exactly; the
            // silence profile is an average and may differ in its last bits.
            Deviation.Within(cache.Cache, Tensor(blob, step, "outSpkcache"), 1e-3, $"step {index} cache");
            Deviation.Within(cache.CachePredictions, Tensor(blob, step, "outSpkcachePreds"), 0.0, $"step {index} cache predictions");
            Deviation.Within(cache.Fifo, Tensor(blob, step, "outFifo"), 0.0, $"step {index} FIFO");
            Deviation.Within(cache.FifoPredictions, Tensor(blob, step, "outFifoPreds"), 0.0, $"step {index} FIFO predictions");
            Deviation.Within(cache.MeanSilence, Tensor(blob, step, "outMeanSilence"), 1e-3, $"step {index} silence profile");
        }
    }

    /// <summary>
    /// What the two boosts are for, checked as a property of the result rather than against the
    /// fixture: every compression reserves exactly twelve slots for mean silence, and every speaker
    /// leaves with a share of the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The twelve is structural and holds for any input — three rows per speaker are appended with
    /// a score of positive infinity, so they always win selection and are always disabled again on
    /// the way out. A port that dropped the silence padding, or appended a large finite score
    /// instead of infinity, changes this number and almost nothing else.
    /// </para>
    /// <para>
    /// The per-speaker range is <b>not</b> a theorem. The strong boost lifts 33 frames per speaker
    /// and the weak boost 66, but the score range is wide enough that an unboosted frame can still
    /// outrank a boosted one, so no bound follows from the constants alone. What is asserted is
    /// that no speaker is shut out and none takes the cache — the failure the boosts exist to
    /// prevent — with the fixture's own numbers as the tighter check.
    /// </para>
    /// </remarks>
    [Fact]
    public void EverySpeakerKeepsAShareOfTheCache()
    {
        var (fixture, blob) = Load();
        var cache = new ArrivalOrderSpeakerCache(fixture.GetProperty("embeddingDimension").GetInt32());
        var chunkPredictions = new float[SortformerGeometry.ChunkLength * SortformerGeometry.SpeakerCount];
        var compressions = 0;

        foreach (var step in fixture.GetProperty("steps").EnumerateArray())
        {
            cache.Update(
                Tensor(blob, step, "chunk"),
                step.GetProperty("physicalChunkFrames").GetInt32(),
                Tensor(blob, step, "preds"),
                step.GetProperty("leftContext").GetInt32(),
                step.GetProperty("rightContext").GetInt32(),
                chunkPredictions);

            if (!cache.HasCompressed)
            {
                continue;
            }

            compressions++;
            var perSpeaker = cache.LastCompressionSlotsPerSpeaker;

            Assert.Equal(
                SortformerGeometry.SilenceFramesPerSpeaker * SortformerGeometry.SpeakerCount,
                cache.LastCompressionSilenceSlots);
            Assert.Equal(
                SortformerGeometry.SpeakerCacheLength - cache.LastCompressionSilenceSlots,
                perSpeaker.Sum());

            for (var speaker = 0; speaker < SortformerGeometry.SpeakerCount; speaker++)
            {
                Assert.InRange(
                    perSpeaker[speaker],
                    SortformerGeometry.StrongBoostPerSpeaker,
                    SortformerGeometry.SpeakerCacheLength / 2);
            }
        }

        Assert.True(compressions >= 9, $"only {compressions} of the fixture's steps compressed the cache");
    }

    /// <summary>
    /// The derived constants, spelled out. They come out of one division and three floors, and a
    /// wrong one silently changes how many frames each speaker keeps.
    /// </summary>
    [Fact]
    public void TheDerivedConstantsAreWhatTheCheckpointImplies()
    {
        Assert.Equal(44, SortformerGeometry.CacheLengthPerSpeaker);
        Assert.Equal(33, SortformerGeometry.StrongBoostPerSpeaker);
        Assert.Equal(66, SortformerGeometry.WeakBoostPerSpeaker);
        Assert.Equal(22, SortformerGeometry.MinimumPositiveScoresPerSpeaker);
        Assert.Equal(3048, SortformerGeometry.MelFramesPerCall);
        Assert.Equal(381, SortformerGeometry.EncoderFramesPerCall);
        Assert.Equal(609, SortformerGeometry.PredictionRows);
    }
}
