namespace Parakeet.Engine.Sortformer;

/// <summary>
/// Every number the Streaming Sortformer pipeline runs on, in one place, with where each came from.
/// </summary>
/// <remarks>
/// <para>
/// Two sets of constants meet here and mixing them is the failure the export's own README warns
/// about. The <b>streaming geometry</b> — cache length, FIFO length, chunk length, the two contexts,
/// the update period — belongs to <i>this export</i>, the <c>default</c> variant, and is read from
/// its <c>config.json</c>; pairing it with another variant's update period evicts the wrong frames
/// while looking healthy. The <b>scoring constants</b> — the score threshold, the two boost rates,
/// the silence threshold — belong to the <i>v2.1 checkpoint</i>. Neither set is defaulted, because a
/// default that silently disagrees with the graph is exactly the failure this class exists to stop.
/// </para>
/// <para>
/// The featurizer's constants come from the <c>preprocessor:</c> block of the checkpoint's own
/// <c>model_config.yaml</c> — unpacked from the <c>.nemo</c> archive, not read off a model card.
/// That is how <see cref="Normalise"/> was caught: it is <c>NA</c>, so this model does not normalise
/// its features, where most NeMo ASR configs say <c>per_feature</c>. Applying per-feature
/// normalisation here makes a correct model look mediocre and breaks nothing.
/// </para>
/// </remarks>
public static class SortformerGeometry
{
    // ── the graph's contract ────────────────────────────────────────────────────────────────

    /// <summary>Speakers the model can tell apart. A hard architectural cap, not a setting.</summary>
    public const int SpeakerCount = 4;

    /// <summary>Width of one embedding the pre-encoder emits.</summary>
    public const int EmbeddingDimension = 512;

    /// <summary>Encoder frames per mel frame: the pre-encoder subsamples 8x.</summary>
    public const int SubsamplingFactor = 8;

    /// <summary>Speaker-cache capacity, in encoder frames.</summary>
    public const int SpeakerCacheLength = 188;

    /// <summary>FIFO capacity, in encoder frames.</summary>
    public const int FifoLength = 40;

    /// <summary>New audio per streaming step, in encoder frames — 340 x 80 ms = 27.2 s.</summary>
    public const int ChunkLength = 340;

    /// <summary>Encoder frames of already-seen audio prepended to each chunk after the first.</summary>
    public const int ChunkLeftContext = 1;

    /// <summary>Encoder frames of not-yet-reported audio appended to each chunk: 3.2 s of lookahead.</summary>
    public const int ChunkRightContext = 40;

    /// <summary>
    /// How many frames the FIFO evicts at once when it overflows. 188 here where NVIDIA's own 30.4 s
    /// preset says 300 — inert for this geometry and measured to be so rather than argued: with
    /// chunk 340 and FIFO 40 the overflow always exceeds both, so the same frames leave either way,
    /// and a ten-minute meeting run under both produced bit-identical probabilities.
    /// </summary>
    public const int SpeakerCacheUpdatePeriod = 188;

    /// <summary>Cache slots reserved per speaker for mean-silence padding.</summary>
    public const int SilenceFramesPerSpeaker = 3;

    /// <summary>Mel frames one graph call takes: <c>(1 + 340 + 40) * 8</c>.</summary>
    public const int MelFramesPerCall = (ChunkLeftContext + ChunkLength + ChunkRightContext) * SubsamplingFactor;

    /// <summary>Encoder frames the graph returns per call, before trimming to the valid length.</summary>
    public const int EncoderFramesPerCall = ChunkLeftContext + ChunkLength + ChunkRightContext;

    /// <summary>Rows in the graph's packed prediction output: cache, then FIFO, then chunk.</summary>
    public const int PredictionRows = SpeakerCacheLength + FifoLength + EncoderFramesPerCall;

    /// <summary>Seconds one prediction frame covers: 8 x the 10 ms hop.</summary>
    public const double FrameSeconds = SubsamplingFactor * 0.01;

    // ── the checkpoint's scoring constants ──────────────────────────────────────────────────

    /// <summary>Floor applied to both p and 1-p before taking logs, so a confident frame's score stays finite.</summary>
    public const float PredictionScoreThreshold = 0.25f;

    /// <summary>Added to every frame that arrived this step, so new audio outranks equally-scored old audio.</summary>
    public const float LatestFrameScoreBoost = 0.05f;

    /// <summary>A frame whose four probabilities sum below this counts towards the mean silence embedding.</summary>
    public const float SilenceThreshold = 0.2f;

    /// <summary>Share of a speaker's cache quota that gets the strong boost, guaranteeing each speaker a floor.</summary>
    public const double StrongBoostRate = 0.75;

    /// <summary>Share that gets the weak boost, which is what stops one speaker filling the cache.</summary>
    public const double WeakBoostRate = 1.5;

    /// <summary>Share of the quota above which a speaker's non-positive (overlapped) frames are dropped entirely.</summary>
    public const double MinimumPositiveScoresRate = 0.5;

    /// <summary>Sentinel standing in for "this top-k slot held no real frame". Never a frame index.</summary>
    public const int MaxIndex = 99999;

    /// <summary>Cache slots one speaker may hold, silence padding excluded: <c>188 / 4 - 3</c>.</summary>
    public const int CacheLengthPerSpeaker = SpeakerCacheLength / SpeakerCount - SilenceFramesPerSpeaker;

    /// <summary>Frames per speaker given the strong boost: <c>floor(44 * 0.75)</c>.</summary>
    public static readonly int StrongBoostPerSpeaker = (int)Math.Floor(CacheLengthPerSpeaker * StrongBoostRate);

    /// <summary>Frames per speaker given the weak boost: <c>floor(44 * 1.5)</c>, deliberately more than the quota.</summary>
    public static readonly int WeakBoostPerSpeaker = (int)Math.Floor(CacheLengthPerSpeaker * WeakBoostRate);

    /// <summary>Positive-scored frames a speaker needs before its overlapped frames stop being eligible.</summary>
    public static readonly int MinimumPositiveScoresPerSpeaker =
        (int)Math.Floor(CacheLengthPerSpeaker * MinimumPositiveScoresRate);

    // ── the featurizer, from the checkpoint's preprocessor block ────────────────────────────

    public const int SampleRate = 16000;

    /// <summary>0.025 s at 16 kHz.</summary>
    public const int WindowSize = 400;

    /// <summary>0.01 s at 16 kHz.</summary>
    public const int WindowStride = 160;

    public const int FftSize = 512;

    public const int MelBands = 128;

    public const float Preemphasis = 0.97f;

    /// <summary>Added inside the log so silence is finite: <c>log_zero_guard_type: add</c>, value 2^-24.</summary>
    public const double LogZeroGuard = 1.0 / (1 << 24);

    /// <summary>Mel frames are padded up to a multiple of this before the graph sees them.</summary>
    public const int PadToMultiple = 16;

    /// <summary>
    /// <c>NA</c>: this model does not normalise its features. Named as a constant so the absence is
    /// a decision on the page rather than a line of code nobody wrote.
    /// </summary>
    public const string Normalise = "NA";

    /// <summary>
    /// The checkpoint asks for <c>dither: 1e-5</c>, and NeMo gates dither on <c>self.training</c>,
    /// so it is a no-op at inference and the features are deterministic. Recorded because "the
    /// config says dither" is otherwise a standing reason to doubt a reproduction.
    /// </summary>
    public const bool DitherAtInference = false;
}
