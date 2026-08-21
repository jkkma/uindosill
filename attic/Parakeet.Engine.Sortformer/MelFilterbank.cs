namespace Parakeet.Engine.Sortformer;

/// <summary>
/// The two tables the mel featurizer multiplies by: a Slaney-normalised mel filterbank and a Hann
/// analysis window. Both are rebuilt here rather than shipped, and both are held against NeMo's own
/// tables by <c>tests/fixtures/diarisation/sortformer/mel-filterbank.f32</c> and
/// <c>mel-window.f32</c>.
/// </summary>
/// <remarks>
/// <para>
/// They are held against fixtures because both have a plausible wrong answer that changes every
/// feature by a few percent and nothing else — no exception, no shape mismatch, just a worse DER.
/// The filterbank's is the mel scale: <c>librosa</c> offers Slaney's piecewise linear-then-log scale
/// and HTK's pure log one, and NeMo asks for Slaney. The window's is its periodicity: NeMo builds it
/// with <c>periodic=false</c>, dividing by <c>N-1</c>, where <c>librosa</c>'s own STFT defaults to
/// <c>periodic=true</c> and divides by <c>N</c>.
/// </para>
/// <para>
/// Built in <see langword="double"/> and stored as <see langword="float"/>, which is what
/// <c>librosa</c> does — it computes the ramps in double and returns a float32 array — so the stored
/// values round the same way.
/// </para>
/// </remarks>
internal static class MelFilterbank
{
    // Slaney's scale is linear below 1 kHz and logarithmic above it. These are the constants
    // librosa's hz_to_mel/mel_to_hz use; naming them keeps the two directions provably inverse.
    private const double LinearHzPerMel = 200.0 / 3.0;
    private const double LogRegionStartHz = 1000.0;
    private const double LogRegionStartMel = LogRegionStartHz / LinearHzPerMel;   // 15
    private static readonly double LogStep = Math.Log(6.4) / 27.0;

    /// <summary>
    /// <c>librosa.filters.mel(sr, n_fft, n_mels, fmin=0, fmax=sr/2, norm="slaney", htk=False)</c>,
    /// as <c>[MelBands][FftSize / 2 + 1]</c>.
    /// </summary>
    public static float[][] Build()
    {
        const int bins = SortformerGeometry.FftSize / 2 + 1;
        const int mels = SortformerGeometry.MelBands;

        // rfftfreq: the centre frequency of each FFT bin.
        var binHz = new double[bins];
        var binWidth = SortformerGeometry.SampleRate / (double)SortformerGeometry.FftSize;
        for (var i = 0; i < bins; i++)
        {
            binHz[i] = i * binWidth;
        }

        // mel_frequencies(n_mels + 2): the lower edge, peak and upper edge of every filter, as a
        // linear sweep in mel space. Two extra points because filter i spans edges i and i+2.
        var edgeHz = new double[mels + 2];
        var lowMel = HzToMel(0.0);
        var highMel = HzToMel(SortformerGeometry.SampleRate / 2.0);

        // numpy's linspace, arithmetic included: one division for the step, then `start + i * step`,
        // with the last point assigned the endpoint rather than computed. Deriving each point as
        // `start + delta * i / (n - 1)` instead is the same value in exact arithmetic and a
        // different one in floating point, and every filter edge would move by an ulp.
        var step = (highMel - lowMel) / (edgeHz.Length - 1);
        for (var i = 0; i < edgeHz.Length; i++)
        {
            edgeHz[i] = MelToHz(i == edgeHz.Length - 1 ? highMel : lowMel + i * step);
        }

        var filters = new float[mels][];
        for (var m = 0; m < mels; m++)
        {
            var row = new float[bins];
            var lowerWidth = edgeHz[m + 1] - edgeHz[m];
            var upperWidth = edgeHz[m + 2] - edgeHz[m + 1];

            // norm="slaney": each filter is scaled to unit area rather than unit peak, so a wide
            // high-frequency filter does not simply out-weigh a narrow low-frequency one.
            var scale = 2.0 / (edgeHz[m + 2] - edgeHz[m]);

            for (var b = 0; b < bins; b++)
            {
                var rising = (binHz[b] - edgeHz[m]) / lowerWidth;
                var falling = (edgeHz[m + 2] - binHz[b]) / upperWidth;
                var weight = Math.Min(rising, falling);

                // Narrowed to float, then scaled, then narrowed again — two roundings, not one.
                // librosa builds the ramps in double but stores them in a float32 array before it
                // applies the normalisation in place, so the intermediate is a float32 value. Doing
                // the whole thing in double and narrowing once is more accurate and lands an ulp
                // away from the table the model was trained against.
                var ramp = weight > 0 ? (float)weight : 0f;
                row[b] = (float)(ramp * scale);
            }

            filters[m] = row;
        }

        return filters;
    }

    /// <summary>
    /// <c>torch.hann_window(WindowSize, periodic: false)</c>, zero-padded up to
    /// <see cref="SortformerGeometry.FftSize"/> and centred within it — which is what
    /// <c>torch.stft</c> does when <c>win_length &lt; n_fft</c>, and getting the alignment wrong
    /// shifts every frame's phase without changing its shape.
    /// </summary>
    public static double[] BuildPaddedWindow()
    {
        var padded = new double[SortformerGeometry.FftSize];
        var offset = (SortformerGeometry.FftSize - SortformerGeometry.WindowSize) / 2;
        for (var n = 0; n < SortformerGeometry.WindowSize; n++)
        {
            padded[offset + n] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / (SortformerGeometry.WindowSize - 1));
        }

        return padded;
    }

    private static double HzToMel(double hz)
    {
        var mel = hz / LinearHzPerMel;
        return hz >= LogRegionStartHz
            ? LogRegionStartMel + Math.Log(hz / LogRegionStartHz) / LogStep
            : mel;
    }

    private static double MelToHz(double mel) =>
        mel >= LogRegionStartMel
            ? LogRegionStartHz * Math.Exp(LogStep * (mel - LogRegionStartMel))
            : LinearHzPerMel * mel;
}
