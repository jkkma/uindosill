namespace Parakeet.Engine.Sortformer.Tests;

/// <summary>
/// The mel featurizer against NeMo's own <c>FilterbankFeatures</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the check that catches the settings which make a correct model look mediocre and break
/// nothing: <c>normalize: NA</c>, the Slaney mel scale rather than HTK's, a <c>periodic=false</c>
/// window, the <c>log(x + 2^-24)</c> guard, and frames past the valid length zeroed. Each has a
/// plausible wrong answer that changes every feature by a few percent, produces no exception, and
/// costs several points of DER.
/// </para>
/// <para>
/// <b>Not bit-exact, and it cannot be.</b> The Python featurizer the spike validated was bit-exact
/// against NeMo because both ran the same PyTorch kernels. This one does not: it computes its
/// transform in double where PyTorch's is single, sums in a different order, and calls a different
/// runtime's <c>log</c>. So the assertion is on the size of the deviation, and the tolerances below
/// are set from what was measured rather than chosen in advance. On log-mel values spanning roughly
/// -16.6 to +5, a deviation of 1e-4 is around one part in 10^5 of the range and far below anything
/// the model resolves.
/// </para>
/// </remarks>
public class FeaturizerTests
{
    /// <summary>
    /// The filterbank, which is where the mel scale would go wrong. Slaney's scale is linear below
    /// 1 kHz and logarithmic above; HTK's is logarithmic throughout, and librosa will build either.
    /// A filterbank on the wrong scale moves every band's centre frequency.
    /// </summary>
    [Fact]
    public void TheFilterbankIsNeMosFilterbank()
    {
        var expected = Fixtures.ReadFloats("mel-filterbank.f32");
        var built = MelFilterbank.Build();

        Assert.Equal(SortformerGeometry.MelBands, built.Length);
        Assert.Equal(SortformerGeometry.FftSize / 2 + 1, built[0].Length);
        Assert.Equal(expected.Length, built.Length * built[0].Length);

        // Exact. The filterbank is a table of products of doubles narrowed to float in a fixed
        // order, so there is nothing here for two runtimes to disagree about once that order
        // matches — which is why the tolerance is zero rather than small.
        var flat = built.SelectMany(row => row).ToArray();
        Deviation.Within(flat, expected, 0.0, "mel filterbank");
    }

    /// <summary>
    /// The window, which is where periodicity would go wrong. NeMo builds it with
    /// <c>periodic=false</c>, dividing by <c>N-1</c>; librosa's own STFT defaults to the periodic
    /// form, dividing by <c>N</c>. And because the 400-sample window is used in a 512-point
    /// transform it has to sit in the middle of the frame, which is what <c>torch.stft</c> does and
    /// what a naive left-alignment would not.
    /// </summary>
    [Fact]
    public void TheWindowIsNeMosWindowAndIsCentredInTheTransform()
    {
        var expected = Fixtures.ReadFloats("mel-window.f32");
        Assert.Equal(SortformerGeometry.WindowSize, expected.Length);

        var padded = MelFilterbank.BuildPaddedWindow();
        Assert.Equal(SortformerGeometry.FftSize, padded.Length);

        const int offset = (SortformerGeometry.FftSize - SortformerGeometry.WindowSize) / 2;
        var inside = new float[SortformerGeometry.WindowSize];
        for (var i = 0; i < inside.Length; i++)
        {
            inside[i] = (float)padded[offset + i];
        }

        // A few ulps, not zero: PyTorch builds this window in single precision, so its cosine is a
        // float32 approximation where this one is a double narrowed at the end. The port's value is
        // the more accurate of the two, and 2e-7 on a multiplicand bounded by 1 is far below
        // anything downstream resolves.
        Deviation.Within(inside, expected, 2e-7, "hann window");

        for (var i = 0; i < padded.Length; i++)
        {
            if (i < offset || i >= offset + SortformerGeometry.WindowSize)
            {
                Assert.Equal(0.0, padded[i]);
            }
        }
    }

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        using var manifest = Fixtures.Manifest();
        foreach (var entry in manifest.RootElement.GetProperty("features").GetProperty("cases").EnumerateArray())
        {
            data.Add(entry.GetProperty("signal").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheFeaturesReproduceWhatNeMoComputed(string signal)
    {
        using var manifest = Fixtures.Manifest();
        var entry = manifest.RootElement.GetProperty("features").GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("signal").GetString() == signal);

        var samples = entry.GetProperty("samples").GetInt32();
        var expectedFrames = entry.GetProperty("shape")[0].GetInt32();
        var expectedValid = entry.GetProperty("validFrames").GetInt32();
        var expected = Fixtures.ReadFloats(entry.GetProperty("file").GetString()!);

        var mel = new MelStream();
        mel.Append(DeterministicInputs.Signal(signal, samples));
        mel.Complete();

        Assert.Equal(expectedValid, mel.ValidFrames);
        Assert.Equal(expectedFrames, mel.PaddedFrames);

        var available = mel.Prepare(0, expectedFrames);
        Assert.Equal(expectedFrames, available);

        var actual = new float[expectedFrames * SortformerGeometry.MelBands];
        for (var frame = 0; frame < expectedFrames; frame++)
        {
            mel.Frame(frame).CopyTo(actual.AsSpan(frame * SortformerGeometry.MelBands));
        }

        Deviation.Within(actual, expected, 1e-3, $"log-mel for '{signal}'");

        // The worst deviation sits where it costs least. A band with almost no energy has
        // `log(sum + 2^-24)` dominated by the guard, so the sum is a cancellation of much larger
        // terms and is exactly where double and single precision part company — and it is also
        // where the model has nothing to hear. Restricted to bands carrying real energy the
        // agreement is tighter, and both numbers are set from measurement rather than chosen: as
        // committed, the worst deviation anywhere is 3.0e-4 and the worst above the guard is 8.0e-5,
        // against log-mel values spanning -16.6 to +5. A tolerance this size is a real guard — get
        // the mel scale, the window periodicity or the normalisation wrong and the deviation is in
        // the tenths, not the ten-thousandths.
        var loud = new List<float>();
        var loudExpected = new List<float>();
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] > -14.0f)
            {
                loud.Add(actual[i]);
                loudExpected.Add(expected[i]);
            }
        }

        if (loud.Count > 0)
        {
            Deviation.Within(
                loud.ToArray(), loudExpected.ToArray(), 2e-4, $"log-mel for '{signal}' in bands above the log guard");
        }
    }

    /// <summary>
    /// Silence is the one case with an exact expected value — every filter sums to zero, so every
    /// band is <c>log(2^-24)</c>. If the guard were applied outside the log, or set to a different
    /// value, this is where it shows.
    /// </summary>
    [Fact]
    public void SilenceIsTheLogOfTheZeroGuard()
    {
        var mel = new MelStream();
        mel.Append(new float[SortformerGeometry.SampleRate]);
        mel.Complete();
        mel.Prepare(0, 1);

        var expected = (float)Math.Log(SortformerGeometry.LogZeroGuard);
        foreach (var value in mel.Frame(0))
        {
            Assert.Equal(expected, value, 6);
        }
    }

    /// <summary>
    /// The stream must give the same features however the caller's audio blocks happen to fall,
    /// which is not free: pre-emphasis carries a sample across every boundary, and the transform
    /// window straddles them. A source that yields one sample at a time is the strongest form of
    /// the test.
    /// </summary>
    [Fact]
    public void BlockBoundariesDoNotMoveTheFeatures()
    {
        const int samples = 8000;
        var signal = DeterministicInputs.Signal("tones", samples);

        var whole = new MelStream();
        whole.Append(signal);
        whole.Complete();

        var split = new MelStream();
        var offset = 0;
        var size = 1;
        while (offset < samples)
        {
            var take = Math.Min(size, samples - offset);
            split.Append(signal.AsSpan(offset, take));
            offset += take;
            size = Math.Min(size * 3 + 1, 997);
        }

        split.Complete();

        var frames = whole.PaddedFrames!.Value;
        Assert.Equal(frames, split.PaddedFrames);
        whole.Prepare(0, frames);
        split.Prepare(0, frames);

        for (var frame = 0; frame < frames; frame++)
        {
            Deviation.Within(split.Frame(frame), whole.Frame(frame), 0.0, $"frame {frame} across block boundaries");
        }
    }
}
