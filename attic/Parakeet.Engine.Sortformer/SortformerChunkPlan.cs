namespace Parakeet.Engine.Sortformer;

/// <summary>One streaming step's slice of the mel spectrogram, and how much of it is real.</summary>
internal readonly record struct SortformerChunkStep
{
    /// <summary>First mel frame of the slice, left context included.</summary>
    public required int MelStart { get; init; }

    /// <summary>Mel frames in the slice: left context, the chunk, and whatever lookahead exists.</summary>
    public required int MelWidth { get; init; }

    /// <summary>Mel frames of the slice holding audio rather than padding, counted from its own start.</summary>
    public required int ChunkLengthFrames { get; init; }

    /// <summary>Encoder frames of left context: 1, or 0 at the start of the recording.</summary>
    public required int LeftContextEncoderFrames { get; init; }

    /// <summary>Encoder frames of lookahead: 40, or fewer as the recording runs out.</summary>
    public required int RightContextEncoderFrames { get; init; }

    /// <summary>First mel frame of the next step.</summary>
    public required int End { get; init; }
}

/// <summary>
/// The chunk loop's index arithmetic, on its own so it can be checked without a model.
/// </summary>
/// <remarks>
/// <para>
/// A transcription of NeMo's <c>streaming_feat_loader</c> as the validated Python driver calls it.
/// It is four lines of clamping with four off-by-one opportunities in it — the left context that
/// does not exist on the first chunk, the lookahead that runs out on the last, the valid length
/// measured from the chunk's own start rather than the recording's, and the encoder-frame rounding,
/// which is <c>round</c> on the left and <c>ceiling</c> on the right — and each of them degrades the
/// result without breaking it. The expected steps for five recording lengths are committed under
/// <c>tests/fixtures/diarisation/sortformer/</c> and the suite replays them.
/// </para>
/// <para>
/// <b>The totals are optional.</b> The reference knows the recording's length before it starts,
/// because it loads the whole file; this runs off a stream and does not, so both totals are passed
/// as null until the audio ends. Every clamp they take part in is inactive while more audio is
/// coming, which is what makes the two agree: a step that is not the last one cannot be shortened
/// by a length it has not reached.
/// </para>
/// </remarks>
internal static class SortformerChunkPlan
{
    private const int Subsampling = SortformerGeometry.SubsamplingFactor;
    private const int MelPerChunk = SortformerGeometry.ChunkLength * Subsampling;
    private const int MelLeftContext = SortformerGeometry.ChunkLeftContext * Subsampling;
    private const int MelRightContext = SortformerGeometry.ChunkRightContext * Subsampling;

    /// <summary>Mel frames a step may need at most, so a reader knows how far to look ahead.</summary>
    public const int MaximumWidth = MelLeftContext + MelPerChunk + MelRightContext;

    /// <summary>
    /// The step beginning at <paramref name="start"/>.
    /// </summary>
    /// <param name="start">First mel frame of new audio; 0, then each previous step's <c>End</c>.</param>
    /// <param name="paddedFrames">
    /// Mel frames the recording has in total, rounded up to a multiple of 16 — null while the audio
    /// is still arriving.
    /// </param>
    /// <param name="validFrames">Mel frames holding audio rather than padding — null on the same terms.</param>
    public static SortformerChunkStep Next(int start, int? paddedFrames, int? validFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);

        var leftOffset = Math.Min(MelLeftContext, start);
        var end = paddedFrames is { } total ? Math.Min(start + MelPerChunk, total) : start + MelPerChunk;
        var rightOffset = paddedFrames is { } known ? Math.Min(MelRightContext, known - end) : MelRightContext;
        var width = end + rightOffset - (start - leftOffset);

        // Counted from the chunk's own start, so the left context counts as audio the chunk holds.
        // long because `validFrames` is unbounded while the length is unknown.
        var valid = validFrames ?? int.MaxValue;
        var length = (int)Math.Clamp((long)valid - start + leftOffset, 0, width);

        return new SortformerChunkStep
        {
            MelStart = start - leftOffset,
            MelWidth = width,
            ChunkLengthFrames = length,
            LeftContextEncoderFrames = (int)Math.Round(leftOffset / (double)Subsampling, MidpointRounding.ToEven),
            RightContextEncoderFrames = (int)Math.Ceiling(rightOffset / (double)Subsampling),
            End = end,
        };
    }
}
