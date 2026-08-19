namespace Parakeet.Engine.Sortformer;

/// <summary>
/// The Arrival-Order Speaker Cache: the bookkeeping that makes speaker 2 the same person at minute
/// thirty as at minute one. A reimplementation of NeMo's
/// <c>SortformerModules.streaming_update_async</c> and the eight helpers under it, for one stream.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The ONNX graph takes the cache and the FIFO as inputs and returns
/// embeddings; it does not update them. Deciding what enters the cache, what waits in the FIFO and
/// what is evicted is the host's job, and a host that skips it gets speaker numbering that restarts
/// every call — the model's whole advantage over a window-local segmenter, thrown away. This is the
/// one part of the pipeline the Python spike did not port: it imported NVIDIA's own function and
/// called it, so what follows is written from the reference source rather than translated from
/// something already working, and it is the highest-risk code in the diariser.
/// </para>
/// <para>
/// <b>How it is held up.</b> Ten steps of NVIDIA's own function, run at the real geometry over
/// embeddings that carry their own coordinates, are committed under
/// <c>tests/fixtures/diarisation/sortformer/</c>, and the suite replays them through this class and
/// compares every tensor. That is what the correctness claim rests on; nothing here is asserted
/// from a reading of the paper.
/// </para>
/// <para>
/// <b>Eviction is not first-in-first-out.</b> Each frame is scored by how confidently exactly one
/// speaker is active — a log-odds score that is positive for clean single-speaker speech and
/// negative for overlap — then boosted twice, once strongly so every speaker keeps a floor of
/// frames and once weakly so no speaker fills the cache, and the highest-scoring 188 survive in
/// chronological order. Slots with no frame worth keeping are filled with a running mean of the
/// embeddings seen during silence.
/// </para>
/// <para>
/// <b>Where it cannot be exact.</b> Two places. Scores are computed in single precision, as the
/// reference does, but <c>log</c> is not guaranteed identical to a last bit between two runtimes,
/// so two frames whose scores differ by an ulp may be ranked differently. And PyTorch's
/// <c>topk</c> does not define its order among equal values, so there is no bit-exact answer to
/// match when frames tie; this class breaks ties towards the earlier frame, which is at least
/// reproducible. Both affect at most which of two equally-scored frames occupies one of 188 slots.
/// </para>
/// </remarks>
internal sealed class ArrivalOrderSpeakerCache
{
    private const int Speakers = SortformerGeometry.SpeakerCount;
    private const int CacheLength = SortformerGeometry.SpeakerCacheLength;
    private const int FifoCapacity = SortformerGeometry.FifoLength;

    private readonly int _embeddingDimension;

    // ── the streaming state ─────────────────────────────────────────────────────────────────
    private readonly float[] _spkcache;
    private readonly float[] _spkcachePreds;
    private readonly float[] _fifo;
    private readonly float[] _fifoPreds;
    private readonly float[] _meanSilence;

    private int _spkcacheLength;
    private int _fifoLength;
    private bool _spkcacheCompressed;
    private long _silenceFrames;

    // ── scratch, allocated once ─────────────────────────────────────────────────────────────
    private float[] _currentSpkcachePreds = [];
    private float[] _currentFifoPreds = [];
    private float[] _chunkPreds = [];
    private float[] _popEmbeddings = [];
    private float[] _popPreds = [];
    private float[] _candidateEmbeddings = [];
    private float[] _candidatePreds = [];
    private float[] _scores = [];
    private float[] _sortKeys = [];
    private float[] _retainedEmbeddings = [];
    private float[] _retainedPreds = [];
    private double[] _silenceSum = [];
    private int[] _order = [];
    private int[] _selected = [];
    private bool[] _disabled = [];

    public ArrivalOrderSpeakerCache(int embeddingDimension = SortformerGeometry.EmbeddingDimension)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(embeddingDimension, 1);
        _embeddingDimension = embeddingDimension;
        _spkcache = new float[CacheLength * embeddingDimension];
        _spkcachePreds = new float[CacheLength * Speakers];
        _fifo = new float[FifoCapacity * embeddingDimension];
        _fifoPreds = new float[FifoCapacity * Speakers];
        _meanSilence = new float[embeddingDimension];
    }

    /// <summary>Frames of the speaker cache that hold real embeddings; the graph is told this.</summary>
    public int CacheFrames => _spkcacheLength;

    /// <summary>Frames waiting in the FIFO; the graph is told this too.</summary>
    public int FifoFrames => _fifoLength;

    /// <summary>Frames counted towards the mean silence embedding so far. Diagnostic.</summary>
    public long SilenceFrames => _silenceFrames;

    /// <summary>Whether the cache has been compressed at least once. Diagnostic.</summary>
    public bool HasCompressed => _spkcacheCompressed;

    /// <summary>
    /// Cache slots the last compression gave each speaker, and how many it filled with mean silence.
    /// </summary>
    /// <remarks>
    /// Not needed to run, and kept because it is the only direct view of what the two boosts
    /// actually did. It cannot be recovered from the cache afterwards: selection is over
    /// (speaker, frame) pairs, so one frame can occupy two slots for two speakers and the stored
    /// predictions do not say which speaker won a slot.
    /// </remarks>
    public IReadOnlyList<int> LastCompressionSlotsPerSpeaker => _slotsPerSpeaker;

    /// <inheritdoc cref="LastCompressionSlotsPerSpeaker"/>
    public int LastCompressionSilenceSlots { get; private set; }

    private readonly int[] _slotsPerSpeaker = new int[Speakers];

    /// <summary>The speaker cache, as the graph takes it: <c>[188 x embeddingDimension]</c>.</summary>
    public ReadOnlySpan<float> Cache => _spkcache;

    /// <summary>The FIFO, as the graph takes it: <c>[40 x embeddingDimension]</c>.</summary>
    public ReadOnlySpan<float> Fifo => _fifo;

    /// <summary>The running mean silence embedding. Diagnostic; the graph never sees it.</summary>
    public ReadOnlySpan<float> MeanSilence => _meanSilence;

    /// <summary>Predictions stored alongside the cache. Diagnostic.</summary>
    public ReadOnlySpan<float> CachePredictions => _spkcachePreds;

    /// <summary>Predictions stored alongside the FIFO. Diagnostic.</summary>
    public ReadOnlySpan<float> FifoPredictions => _fifoPreds;

    /// <summary>
    /// Consumes one chunk of embeddings and the predictions the graph made over
    /// <c>[cache | FIFO | chunk]</c>, advances the state, and writes the chunk's own predictions —
    /// the only part of the output that is a result rather than bookkeeping — into
    /// <paramref name="chunkPredictions"/>.
    /// </summary>
    /// <param name="chunk">
    /// <c>[physicalFrames x embeddingDimension]</c>, including <paramref name="leftContext"/> frames
    /// of already-seen audio at the front and <paramref name="rightContext"/> frames of lookahead at
    /// the back. Both are excluded from the state update: the left context is already in the FIFO
    /// and the right context has not been reported yet.
    /// </param>
    /// <param name="physicalFrames">
    /// Rows of <paramref name="chunk"/> that exist. This is the graph's <c>chunk_pre_encode_lengths</c>
    /// rather than its fixed 381-row output width: the reference derives the chunk's capacity from
    /// the tensor it is handed, so passing the full width over-counts by one on the first chunk and
    /// by more on the last.
    /// </param>
    /// <param name="predictions">
    /// The graph's packed output, <c>[<see cref="SortformerGeometry.PredictionRows"/> x 4]</c>: the
    /// valid cache frames, then the valid FIFO frames, then the chunk. The offsets are the state's
    /// current lengths, not the physical capacities.
    /// </param>
    /// <returns>Frames written to <paramref name="chunkPredictions"/>.</returns>
    public int Update(
        ReadOnlySpan<float> chunk,
        int physicalFrames,
        ReadOnlySpan<float> predictions,
        int leftContext,
        int rightContext,
        Span<float> chunkPredictions)
    {
        var maxChunkLength = physicalFrames - leftContext - rightContext;
        if (maxChunkLength <= 0)
        {
            return 0;
        }

        // A finalised stream can flush its whole FIFO, so the pop buffer must fit it; it never needs
        // more than everything there is.
        var maxPopLength = Math.Min(
            Math.Max(SortformerGeometry.SpeakerCacheUpdatePeriod, Math.Max(FifoCapacity, maxChunkLength)),
            maxChunkLength + FifoCapacity);

        var chunkLength = Math.Clamp(physicalFrames - leftContext, 0, maxChunkLength);

        EnsureScratch(maxChunkLength, maxPopLength);

        GatherPredictions(predictions, maxChunkLength, chunkLength, leftContext);

        var (popLength, newFifoLength) = FifoPopLengths(chunkLength);

        UpdateFifo(chunk, leftContext, maxChunkLength, maxPopLength, popLength, newFifoLength);
        UpdateSilenceProfile(maxPopLength, popLength);
        UpdateCache(maxPopLength, popLength);

        var written = Math.Min(maxChunkLength, chunkPredictions.Length / Speakers);
        _chunkPreds.AsSpan(0, written * Speakers).CopyTo(chunkPredictions);
        return written;
    }

    /// <summary>
    /// Maps the graph's packed predictions back onto rectangular cache, FIFO and chunk regions.
    /// Rows past each region's valid length are zeroed rather than left holding whatever the packing
    /// put there.
    /// </summary>
    private void GatherPredictions(ReadOnlySpan<float> predictions, int maxChunkLength, int chunkLength, int leftContext)
    {
        CopyRows(predictions, 0, _currentSpkcachePreds, CacheLength, _spkcacheLength, Speakers);
        CopyRows(predictions, _spkcacheLength, _currentFifoPreds, FifoCapacity, _fifoLength, Speakers);
        CopyRows(
            predictions,
            _spkcacheLength + _fifoLength + leftContext,
            _chunkPreds,
            maxChunkLength,
            chunkLength,
            Speakers);

        static void CopyRows(ReadOnlySpan<float> source, int sourceRow, float[] destination, int rows, int valid, int width)
        {
            for (var i = 0; i < rows; i++)
            {
                var target = destination.AsSpan(i * width, width);
                if (i < valid)
                {
                    source.Slice((sourceRow + i) * width, width).CopyTo(target);
                }
                else
                {
                    target.Clear();
                }
            }
        }
    }

    /// <summary>
    /// How many logical <c>[FIFO | chunk]</c> frames leave for the cache this step, and how many
    /// stay behind.
    /// </summary>
    /// <remarks>
    /// The FIFO does not drain one frame at a time. Once it overflows, it evicts a whole update
    /// period at once — or the overflow, when that is larger — which is what makes the cache
    /// compress in bursts rather than continuously. At this export's geometry the overflow always
    /// exceeds the period, so 340 frames leave per step and 40 remain.
    /// </remarks>
    private (int PopLength, int NewFifoLength) FifoPopLengths(int chunkLength)
    {
        var combined = _fifoLength + chunkLength;
        var overflow = combined - FifoCapacity;
        var pop = combined > FifoCapacity
            ? Math.Min(combined, Math.Max(SortformerGeometry.SpeakerCacheUpdatePeriod, overflow))
            : 0;

        // A chunk with nothing in it marks a finalised stream: flush what is left.
        if (chunkLength == 0)
        {
            pop = _fifoLength;
        }

        return (pop, combined - pop);
    }

    /// <summary>
    /// Splits the logical <c>[old FIFO | this chunk]</c> sequence into the frames that leave and the
    /// frames that stay, and installs the latter as the new FIFO.
    /// </summary>
    private void UpdateFifo(
        ReadOnlySpan<float> chunk,
        int leftContext,
        int maxChunkLength,
        int maxPopLength,
        int popLength,
        int newFifoLength)
    {
        var dimension = _embeddingDimension;

        for (var i = 0; i < maxPopLength; i++)
        {
            Resolve(chunk, leftContext, maxChunkLength, i, i < popLength, _popEmbeddings, _popPreds, i);
        }

        // The frames that stay are read out of the old FIFO, so they land in scratch first: writing
        // them straight back would overwrite a frame this loop has not read yet.
        for (var i = 0; i < FifoCapacity; i++)
        {
            Resolve(chunk, leftContext, maxChunkLength, popLength + i, i < newFifoLength, _retainedEmbeddings, _retainedPreds, i);
        }

        _retainedEmbeddings.AsSpan(0, FifoCapacity * dimension).CopyTo(_fifo);
        _retainedPreds.AsSpan(0, FifoCapacity * Speakers).CopyTo(_fifoPreds);
        _fifoLength = newFifoLength;
    }

    /// <summary>
    /// Writes one frame of the logical <c>[old FIFO | this chunk]</c> sequence into slot
    /// <paramref name="slot"/> of a destination pair, or zeroes that slot when the frame is padding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Logical index <paramref name="logical"/> addresses the old FIFO below its valid length and
    /// this chunk above it. The chunk's own left and right context are not part of that sequence:
    /// the left context is already in the FIFO, and the right context is lookahead the model has not
    /// committed to. A method rather than a local function because a <c>Span</c> cannot be captured.
    /// </para>
    /// <para>
    /// <b>A FIFO frame's predictions are this step's, not the ones it arrived with.</b> The graph
    /// re-predicts the whole <c>[cache | FIFO | chunk]</c> window every call, so a frame that has sat
    /// in the FIFO is scored again with the benefit of everything heard since — which is the point of
    /// keeping it there rather than committing it straight to the cache. The reference reads
    /// <c>current_fifo_preds</c>, the tensor <c>_gather_async_predictions</c> just produced; its
    /// stored <c>fifo_preds</c> is written and then never read again in the asynchronous path, and
    /// only the synchronous one uses it. Reading the stored copy here instead scores every FIFO
    /// frame with stale probabilities, which corrupts the silence profile and evicts the wrong
    /// frames — no exception, a worse DER.
    /// </para>
    /// </remarks>
    private void Resolve(
        ReadOnlySpan<float> chunk,
        int leftContext,
        int maxChunkLength,
        int logical,
        bool valid,
        float[] embeddings,
        float[] predictions,
        int slot)
    {
        var dimension = _embeddingDimension;
        var embedding = embeddings.AsSpan(slot * dimension, dimension);
        var prediction = predictions.AsSpan(slot * Speakers, Speakers);

        if (valid)
        {
            if (logical < _fifoLength)
            {
                _fifo.AsSpan(logical * dimension, dimension).CopyTo(embedding);
                _currentFifoPreds.AsSpan(logical * Speakers, Speakers).CopyTo(prediction);
                return;
            }

            var withinChunk = logical - _fifoLength;
            if (withinChunk < maxChunkLength)
            {
                chunk.Slice((leftContext + withinChunk) * dimension, dimension).CopyTo(embedding);
                _chunkPreds.AsSpan(withinChunk * Speakers, Speakers).CopyTo(prediction);
                return;
            }
        }

        embedding.Clear();
        prediction.Clear();
    }

    /// <summary>
    /// Folds the frames that just left the FIFO into the running mean of silent embeddings, which is
    /// what fills a cache slot that no frame earned.
    /// </summary>
    private void UpdateSilenceProfile(int maxPopLength, int popLength)
    {
        var dimension = _embeddingDimension;
        var silent = 0L;
        var sum = _silenceSum;
        Array.Clear(sum, 0, dimension);

        for (var i = 0; i < Math.Min(popLength, maxPopLength); i++)
        {
            var prediction = _popPreds.AsSpan(i * Speakers, Speakers);
            var total = 0f;
            for (var s = 0; s < Speakers; s++)
            {
                total += prediction[s];
            }

            if (total >= SortformerGeometry.SilenceThreshold)
            {
                continue;
            }

            silent++;
            var embedding = _popEmbeddings.AsSpan(i * dimension, dimension);
            for (var d = 0; d < dimension; d++)
            {
                sum[d] += embedding[d];
            }
        }

        if (silent == 0)
        {
            return;
        }

        var updated = _silenceFrames + silent;
        for (var d = 0; d < dimension; d++)
        {
            _meanSilence[d] = (float)((_meanSilence[d] * (double)_silenceFrames + sum[d]) / Math.Max(1L, updated));
        }

        _silenceFrames = updated;
    }

    /// <summary>
    /// Appends the popped frames to the cache and compresses back down to 188 when they overflow it.
    /// </summary>
    private void UpdateCache(int maxPopLength, int popLength)
    {
        var dimension = _embeddingDimension;
        var candidateRows = CacheLength + maxPopLength;
        var updatedLength = _spkcacheLength + popLength;
        var needCompress = updatedLength > CacheLength;

        // The first compression scores the cache with predictions made *this* step, which see the
        // whole recording so far; every later one keeps the predictions stored when each frame was
        // admitted. The latch is what makes those two cases distinguishable.
        var firstCompression = !_spkcacheCompressed && needCompress;
        var oldPredictions = firstCompression ? _currentSpkcachePreds : _spkcachePreds;

        for (var i = 0; i < candidateRows; i++)
        {
            var embedding = _candidateEmbeddings.AsSpan(i * dimension, dimension);
            var prediction = _candidatePreds.AsSpan(i * Speakers, Speakers);

            if (i >= updatedLength)
            {
                embedding.Clear();
                prediction.Clear();
                continue;
            }

            if (i < _spkcacheLength)
            {
                _spkcache.AsSpan(i * dimension, dimension).CopyTo(embedding);
                oldPredictions.AsSpan(i * Speakers, Speakers).CopyTo(prediction);
                continue;
            }

            var popped = i - _spkcacheLength;
            if (popped < maxPopLength)
            {
                _popEmbeddings.AsSpan(popped * dimension, dimension).CopyTo(embedding);
                _popPreds.AsSpan(popped * Speakers, Speakers).CopyTo(prediction);
            }
            else
            {
                embedding.Clear();
                prediction.Clear();
            }
        }

        if (needCompress)
        {
            Compress(candidateRows);
            _spkcacheCompressed = true;
        }
        else
        {
            _candidateEmbeddings.AsSpan(0, CacheLength * dimension).CopyTo(_spkcache);
            _candidatePreds.AsSpan(0, CacheLength * Speakers).CopyTo(_spkcachePreds);
        }

        _spkcacheLength = Math.Min(updatedLength, CacheLength);
    }

    /// <summary>
    /// Keeps the 188 most useful of <paramref name="rows"/> frames, ordered by speaker and then
    /// chronologically within each speaker.
    /// </summary>
    private void Compress(int rows)
    {
        var dimension = _embeddingDimension;
        var padded = rows + SortformerGeometry.SilenceFramesPerSpeaker;

        ScoreFrames(rows, padded);

        // Flattened as speaker-major, which is what makes the ascending sort below group the cache
        // by speaker and keep each speaker's frames in the order they were spoken.
        SelectTopK(_scores, padded * Speakers, CacheLength, _selected);

        for (var i = 0; i < CacheLength; i++)
        {
            var flat = _selected[i];
            _selected[i] = float.IsNegativeInfinity(_scores[flat]) ? SortformerGeometry.MaxIndex : flat;
        }

        Array.Sort(_selected, 0, CacheLength);

        Array.Clear(_slotsPerSpeaker);
        LastCompressionSilenceSlots = 0;

        for (var i = 0; i < CacheLength; i++)
        {
            var frame = _selected[i] % padded;
            var disabled = _selected[i] == SortformerGeometry.MaxIndex || frame >= rows;

            if (disabled)
            {
                LastCompressionSilenceSlots++;
            }
            else
            {
                // Speaker-major flattening: the slot's owner is which block of `padded` it fell in.
                _slotsPerSpeaker[_selected[i] / padded]++;
            }

            _disabled[i] = disabled;
            _selected[i] = disabled ? 0 : frame;
        }

        for (var i = 0; i < CacheLength; i++)
        {
            var target = _spkcache.AsSpan(i * dimension, dimension);
            var predictions = _spkcachePreds.AsSpan(i * Speakers, Speakers);

            if (_disabled[i])
            {
                _meanSilence.CopyTo(target);
                predictions.Clear();
                continue;
            }

            _candidateEmbeddings.AsSpan(_selected[i] * dimension, dimension).CopyTo(target);
            _candidatePreds.AsSpan(_selected[i] * Speakers, Speakers).CopyTo(predictions);
        }
    }

    /// <summary>
    /// Scores every (frame, speaker) pair by how confidently that speaker and no other is talking,
    /// then applies the disabling rules, the recency bonus and the two boosts.
    /// </summary>
    /// <remarks>
    /// The score is the log-odds of "this speaker is active" plus the log-odds of every other
    /// speaker being silent, so it is positive for clean single-speaker speech and negative for
    /// overlap. Both probabilities are floored at 0.25 before the log, which stops one very
    /// confident frame dominating the ranking outright.
    /// </remarks>
    private void ScoreFrames(int rows, int padded)
    {
        var scores = _scores;
        Array.Clear(scores, 0, padded * Speakers);

        const float floor = SortformerGeometry.PredictionScoreThreshold;
        var half = -MathF.Log(0.5f);

        Span<float> logProbability = stackalloc float[Speakers];
        Span<float> logComplement = stackalloc float[Speakers];

        for (var f = 0; f < rows; f++)
        {
            var predictions = _candidatePreds.AsSpan(f * Speakers, Speakers);
            var complementSum = 0f;

            for (var s = 0; s < Speakers; s++)
            {
                logProbability[s] = MathF.Log(Math.Max(predictions[s], floor));
                logComplement[s] = MathF.Log(Math.Max(1f - predictions[s], floor));
                complementSum += logComplement[s];
            }

            for (var s = 0; s < Speakers; s++)
            {
                scores[s * padded + f] = logProbability[s] - logComplement[s] + complementSum + half;
            }
        }

        // Non-speech is never worth caching, and neither is overlapped speech once a speaker has
        // enough clean frames to fill its quota from.
        for (var s = 0; s < Speakers; s++)
        {
            var offset = s * padded;
            var positive = 0;
            for (var f = 0; f < rows; f++)
            {
                var speech = _candidatePreds[f * Speakers + s] > 0.5f;
                if (!speech)
                {
                    scores[offset + f] = float.NegativeInfinity;
                }
                else if (scores[offset + f] > 0f)
                {
                    positive++;
                }
            }

            if (positive >= SortformerGeometry.MinimumPositiveScoresPerSpeaker)
            {
                for (var f = 0; f < rows; f++)
                {
                    if (_candidatePreds[f * Speakers + s] > 0.5f && !(scores[offset + f] > 0f))
                    {
                        scores[offset + f] = float.NegativeInfinity;
                    }
                }
            }
        }

        // Frames that arrived this step outrank equally-scored frames already in the cache, so a
        // long-running cache does not freeze against new evidence.
        for (var s = 0; s < Speakers; s++)
        {
            for (var f = CacheLength; f < rows; f++)
            {
                scores[s * padded + f] += SortformerGeometry.LatestFrameScoreBoost;
            }
        }

        // Strong first, then weak over the already-boosted scores: the order matters, because the
        // second selection sees the first's effect. A frame in both sets gets both.
        Boost(scores, rows, padded, SortformerGeometry.StrongBoostPerSpeaker, 2f * half);
        Boost(scores, rows, padded, SortformerGeometry.WeakBoostPerSpeaker, half);

        // Trailing rows scoring +inf reserve three slots per speaker for the mean silence
        // embedding, so a cache holding few real speakers is not padded with weak frames.
        for (var s = 0; s < Speakers; s++)
        {
            for (var f = rows; f < padded; f++)
            {
                scores[s * padded + f] = float.PositiveInfinity;
            }
        }
    }

    /// <summary>Lifts the <paramref name="count"/> best-scoring frames of each speaker by <paramref name="amount"/>.</summary>
    private void Boost(float[] scores, int rows, int padded, int count, float amount)
    {
        if (count <= 0)
        {
            return;
        }

        for (var s = 0; s < Speakers; s++)
        {
            var offset = s * padded;
            var take = Math.Min(count, rows);
            SelectTopK(scores.AsSpan(offset, rows), rows, take, _selected);
            for (var i = 0; i < take; i++)
            {
                // Disabled frames stay disabled: -inf plus anything finite is still -inf.
                scores[offset + _selected[i]] += amount;
            }
        }
    }

    /// <summary>
    /// Writes the indices of the <paramref name="k"/> largest of <paramref name="count"/> values
    /// into <paramref name="destination"/>, highest first, earlier index winning a tie.
    /// </summary>
    /// <remarks>
    /// The tie rule is this implementation's, not the reference's: PyTorch's <c>topk</c> leaves the
    /// order among equal values unspecified, so there is no bit-exact behaviour available to copy.
    /// Choosing the earlier frame at least makes the result reproducible, and the difference can
    /// only ever be which of two identically-scored frames takes a slot.
    /// </remarks>
    private void SelectTopK(ReadOnlySpan<float> values, int count, int k, int[] destination)
    {
        if (_order.Length < count)
        {
            _order = new int[count];
            _sortKeys = new float[count];
            _descending = null; // the comparer closes over the array, not over the field
        }

        var order = _order;
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        // Sorting a copy of the keys rather than reading through the index: Array.Sort's comparer
        // is called with the payload, so the keys have to be addressable by index, and the caller's
        // span may be a window into a longer buffer.
        values[..count].CopyTo(_sortKeys);
        Array.Sort(_order, 0, count, _descending ??= new DescendingByValue(_sortKeys));

        for (var i = 0; i < k; i++)
        {
            destination[i] = order[i];
        }
    }

    private DescendingByValue? _descending;

    /// <summary>Highest value first; equal values ordered by index, so the result is reproducible.</summary>
    private sealed class DescendingByValue(float[] keys) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            var byValue = keys[y].CompareTo(keys[x]);
            return byValue != 0 ? byValue : x.CompareTo(y);
        }
    }

    private void EnsureScratch(int maxChunkLength, int maxPopLength)
    {
        var dimension = _embeddingDimension;
        var candidateRows = CacheLength + maxPopLength;
        var paddedRows = candidateRows + SortformerGeometry.SilenceFramesPerSpeaker;

        Grow(ref _currentSpkcachePreds, CacheLength * Speakers);
        Grow(ref _currentFifoPreds, FifoCapacity * Speakers);
        Grow(ref _chunkPreds, maxChunkLength * Speakers);
        Grow(ref _popEmbeddings, maxPopLength * dimension);
        Grow(ref _popPreds, maxPopLength * Speakers);
        Grow(ref _candidateEmbeddings, candidateRows * dimension);
        Grow(ref _candidatePreds, candidateRows * Speakers);
        Grow(ref _scores, paddedRows * Speakers);
        Grow(ref _retainedEmbeddings, FifoCapacity * dimension);
        Grow(ref _retainedPreds, FifoCapacity * Speakers);

        if (_silenceSum.Length < dimension)
        {
            _silenceSum = new double[dimension];
        }

        if (_selected.Length < Math.Max(CacheLength, paddedRows * Speakers))
        {
            _selected = new int[Math.Max(CacheLength, paddedRows * Speakers)];
        }

        if (_disabled.Length < CacheLength)
        {
            _disabled = new bool[CacheLength];
        }

        static void Grow(ref float[] buffer, int required)
        {
            if (buffer.Length < required)
            {
                buffer = new float[required];
            }
        }
    }
}
