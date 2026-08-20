namespace Parakeet.Engine.Marian;

/// <summary>
/// The beam search that produced every translation figure this project publishes.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a port of one implementation, not of the idea of beam search.</b> The two ONNX graphs
/// are pinned by digest; the search over them is not, and it is a real degree of freedom. Whether a
/// finished hypothesis is scored by its total or its mean log probability, whether the loop stops
/// when the beams are full or when they can no longer improve, how equal-scoring candidates are
/// ordered, whether a beam that has just emitted the end token can still be continued — each is a
/// choice, and a loop that differs in any of them produces different English while looking entirely
/// correct. The diariser has already shown what that costs on this project: one numerical tie-break
/// moved a meeting by 11 DER points.
/// </para>
/// <para>
/// So this reproduces transformers 4.57.6's <c>GenerationMixin._beam_search</c> — the vectorised
/// rewrite, not the older <c>BeamSearchScorer</c>, which is a different algorithm with the same
/// name. Its shape is worth stating because it is not the textbook one: at every step it keeps
/// <b>2 × beams</b> candidates rather than <c>beams</c>, so that a step in which every top beam
/// ends the sentence still leaves live continuations; finished hypotheses live in a second set of
/// <c>beams</c> slots that new ones must outscore to enter; and only a candidate from the top
/// <c>beams</c> of the step may enter that set at all, the rest existing purely as continuation
/// fodder.
/// </para>
/// <para>
/// <b>What is checked, and what that check is worth.</b> The agreement between this and the
/// recorded hypotheses is measured rather than asserted — see
/// <c>scripts/measure-translation-agreement.ps1</c> and <c>docs/UNPROVEN.md</c>. The step logic
/// itself is held to scripted logits in the test project, where a machine with no weights can
/// exercise the cases that are hard to reach with a real model: a beam finishing first and being
/// displaced later, the length penalty deciding between a short hypothesis and a long one, and the
/// banned token never being emitted.
/// </para>
/// </remarks>
internal static class MarianBeamSearch
{
    /// <summary>
    /// The very negative number the reference uses to park a candidate out of contention.
    /// </summary>
    /// <remarks>
    /// −1e9 and not negative infinity, and the difference is load-bearing: parked scores are still
    /// added to and compared against each other, so they have to stay finite and ordered. This is
    /// the reference's constant, kept in single precision because that is the precision the whole
    /// score arithmetic runs in there.
    /// </remarks>
    private const float Parked = -1.0e9f;

    /// <summary>Length of the prompt the decoder starts from: the start token, and nothing else.</summary>
    private const int DecoderPromptLength = 1;

    /// <summary>
    /// Decodes one source and returns the winning token sequence, start token first and end token
    /// last.
    /// </summary>
    public static IReadOnlyList<int> Search(
        IMarianDecoder decoder,
        MarianConfiguration configuration,
        IReadOnlyList<int> sourceIds,
        MarianDecodeSettings settings,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        if (sourceIds.Count == 0)
        {
            // Named here rather than left to whichever decoder is behind the interface, because
            // "the encoder was handed nothing" is a caller's mistake and reads like a model failure
            // by the time ONNX Runtime reports it.
            throw new ArgumentException(
                "A source with no tokens has nothing to translate.", nameof(sourceIds));
        }

        var beams = settings.Beams;
        var vocabulary = decoder.VocabularySize;
        var maxLength = DecoderPromptLength + settings.MaxNewTokens;
        var eos = configuration.EndOfSequenceTokenId;

        // 2 x beams, from `max(2, 1 + number of end tokens) * beams`. Gathering only `beams` here
        // would let a step in which all of them end the sentence leave nothing to continue with.
        var toKeep = Math.Max(2, 1 + 1) * beams;

        var runningSequences = new int[beams * maxLength];
        var sequences = new int[beams * maxLength];
        Array.Fill(runningSequences, configuration.PadTokenId);
        for (var beam = 0; beam < beams; beam++)
        {
            runningSequences[beam * maxLength] = configuration.DecoderStartTokenId;
        }

        Array.Copy(runningSequences, sequences, runningSequences.Length);

        // Beam 0 starts at zero and the rest parked, so that the first step ranks one distribution
        // rather than `beams` identical ones and picks the same token `beams` times.
        var runningBeamScores = new float[beams];
        var beamScores = new float[beams];
        for (var beam = 0; beam < beams; beam++)
        {
            runningBeamScores[beam] = beam == 0 ? 0f : Parked;
            beamScores[beam] = Parked;
        }

        var isSentFinished = new bool[beams];
        var runningBeamIndices = new int[beams * (maxLength - DecoderPromptLength)];
        var beamIndices = new int[beams * (maxLength - DecoderPromptLength)];
        Array.Fill(runningBeamIndices, -1);
        Array.Fill(beamIndices, -1);

        var improvementPossible = true;

        var logProbabilities = new float[beams * vocabulary];
        var accumulated = new float[beams * vocabulary];
        var topIndices = new int[toKeep];
        var topScores = new float[toKeep];
        var topSequences = new int[toKeep * maxLength];
        var topBeamIndices = new int[toKeep * (maxLength - DecoderPromptLength)];
        var topBeamOf = new int[toKeep];
        var hitsStoppingCriteria = new bool[toKeep];
        var runningCandidateScores = new float[toKeep];
        var nextBeams = new int[beams];
        var order = new int[beams];
        var tokens = new long[beams];

        // beams existing slots + toKeep new candidates, ranked together every step.
        var mergedScores = new float[beams + toKeep];
        var mergedIsFinished = new bool[beams + toKeep];
        var mergedPick = new int[beams];
        var nextSequences = new int[beams * maxLength];
        var nextBeamIndices = new int[beams * (maxLength - DecoderPromptLength)];
        var nextIsFinished = new bool[beams];
        var nextScores = new float[beams];

        decoder.Begin(sourceIds, beams);

        var currentLength = DecoderPromptLength;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            for (var beam = 0; beam < beams; beam++)
            {
                tokens[beam] = runningSequences[(beam * maxLength) + currentLength - 1];
            }

            var logits = decoder.Step(tokens);

            for (var beam = 0; beam < beams; beam++)
            {
                LogSoftmax(logits.Slice(beam * vocabulary, vocabulary), logProbabilities.AsSpan(beam * vocabulary, vocabulary));
            }

            Process(logProbabilities, configuration, beams, vocabulary, currentLength, maxLength);

            for (var beam = 0; beam < beams; beam++)
            {
                var score = runningBeamScores[beam];
                var from = logProbabilities.AsSpan(beam * vocabulary, vocabulary);
                var into = accumulated.AsSpan(beam * vocabulary, vocabulary);
                for (var token = 0; token < vocabulary; token++)
                {
                    into[token] = from[token] + score;
                }
            }

            TopK(accumulated, toKeep, topIndices, topScores);

            for (var k = 0; k < toKeep; k++)
            {
                var beam = topIndices[k] / vocabulary;
                var token = topIndices[k] % vocabulary;
                topBeamOf[k] = beam;

                Array.Copy(runningSequences, beam * maxLength, topSequences, k * maxLength, maxLength);
                topSequences[(k * maxLength) + currentLength] = token;

                Array.Copy(
                    runningBeamIndices,
                    beam * (maxLength - DecoderPromptLength),
                    topBeamIndices,
                    k * (maxLength - DecoderPromptLength),
                    maxLength - DecoderPromptLength);
                topBeamIndices[(k * (maxLength - DecoderPromptLength)) + currentLength - DecoderPromptLength] = beam;

                // The end token, or nowhere left to put another one.
                hitsStoppingCriteria[k] = token == eos || currentLength + 1 >= maxLength;
            }

            // The beams that carry on. A candidate that just finished is parked so it cannot be
            // continued as well as banked.
            for (var k = 0; k < toKeep; k++)
            {
                runningCandidateScores[k] = topScores[k] + (hitsStoppingCriteria[k] ? Parked : 0f);
            }

            TopK(runningCandidateScores, beams, nextBeams, nextScores);

            for (var beam = 0; beam < beams; beam++)
            {
                var k = nextBeams[beam];
                Array.Copy(topSequences, k * maxLength, nextSequences, beam * maxLength, maxLength);
                Array.Copy(
                    topBeamIndices,
                    k * (maxLength - DecoderPromptLength),
                    nextBeamIndices,
                    beam * (maxLength - DecoderPromptLength),
                    maxLength - DecoderPromptLength);
                runningBeamScores[beam] = runningCandidateScores[k];
                order[beam] = topBeamOf[k];
            }

            (runningSequences, nextSequences) = (nextSequences, runningSequences);
            (runningBeamIndices, nextBeamIndices) = (nextBeamIndices, runningBeamIndices);

            // The finished set. Only a candidate from the step's top `beams` may enter it: the rest
            // exist to keep the search alive, and banking one would let a hypothesis nobody ranked
            // that highly win the sentence.
            var length = (float)Math.Pow(currentLength + 1 - DecoderPromptLength, settings.LengthPenalty);
            var everyBeamFinished = settings.EarlyStopping && All(isSentFinished);

            for (var beam = 0; beam < beams; beam++)
            {
                mergedScores[beam] = beamScores[beam];
                mergedIsFinished[beam] = isSentFinished[beam];
            }

            for (var k = 0; k < toKeep; k++)
            {
                var eligible = hitsStoppingCriteria[k] && k < beams;
                var score = topScores[k] / length;

                if (everyBeamFinished)
                {
                    score += Parked;
                }

                if (!improvementPossible)
                {
                    score += Parked;
                }

                if (!eligible)
                {
                    score += Parked;
                }

                mergedScores[beams + k] = score;
                mergedIsFinished[beams + k] = eligible;
            }

            TopK(mergedScores, beams, mergedPick, nextScores);

            for (var beam = 0; beam < beams; beam++)
            {
                var pick = mergedPick[beam];
                var source = pick < beams ? sequences : topSequences;
                var sourceRow = pick < beams ? pick : pick - beams;

                Array.Copy(source, sourceRow * maxLength, nextSequences, beam * maxLength, maxLength);

                var indexSource = pick < beams ? beamIndices : topBeamIndices;
                Array.Copy(
                    indexSource,
                    sourceRow * (maxLength - DecoderPromptLength),
                    nextBeamIndices,
                    beam * (maxLength - DecoderPromptLength),
                    maxLength - DecoderPromptLength);

                nextIsFinished[beam] = mergedIsFinished[pick];
            }

            (sequences, nextSequences) = (nextSequences, sequences);
            (beamIndices, nextBeamIndices) = (nextBeamIndices, beamIndices);
            (isSentFinished, nextIsFinished) = (nextIsFinished, isSentFinished);
            Array.Copy(nextScores, beamScores, beams);

            decoder.Reorder(order);

            currentLength++;

            improvementPossible = CanStillImprove(
                improvementPossible, runningBeamScores, beamScores, isSentFinished,
                currentLength, maxLength, settings);

            // Three ways to be done: no open beam can beat the finished set; every beam has
            // finished and early stopping was asked for; or nothing may be continued at all.
            var openBeamExists = !(settings.EarlyStopping && All(isSentFinished));
            if (!improvementPossible || !openBeamExists || All(hitsStoppingCriteria))
            {
                break;
            }
        }

        // The winner is slot zero: the finished set is kept ranked. Its length is how many tokens
        // it actually generated, which is not the loop's length — the beam that wins may have
        // finished several steps before the loop stopped.
        var generated = 0;
        for (var i = 0; i < maxLength - DecoderPromptLength; i++)
        {
            if (beamIndices[i] != -1)
            {
                generated++;
            }
        }

        var output = new int[DecoderPromptLength + generated];
        Array.Copy(sequences, 0, output, 0, output.Length);
        return output;
    }

    /// <summary>
    /// Whether an open beam could still beat the worst finished hypothesis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Once false this stays false, which is why it is threaded through rather than recomputed: it
    /// is a latch in the reference too, and it does more than end the loop — while it is false, no
    /// further hypothesis may enter the finished set.
    /// </para>
    /// <para>
    /// <b>The worst finished score is the minimum over every slot, including the empty ones.</b>
    /// That is the reference's arithmetic and not a simplification of it: an unfilled slot holds
    /// −1e9, so until all <c>beams</c> slots hold a real hypothesis the worst is −1e9 and nothing
    /// can fail to beat it. The practical effect is that the search never stops early until it has
    /// found <c>beams</c> complete hypotheses, and reproducing that is the point.
    /// </para>
    /// </remarks>
    private static bool CanStillImprove(
        bool improvementPossible,
        ReadOnlySpan<float> runningBeamScores,
        ReadOnlySpan<float> beamScores,
        ReadOnlySpan<bool> isSentFinished,
        int currentLength,
        int maxLength,
        MarianDecodeSettings settings)
    {
        if (!improvementPossible)
        {
            return false;
        }

        var hypotheticalLength = settings.EarlyStopping && settings.LengthPenalty > 0f
            ? maxLength - DecoderPromptLength
            : currentLength - DecoderPromptLength;

        var best = runningBeamScores[0] / (float)Math.Pow(hypotheticalLength, settings.LengthPenalty);

        var worst = float.MaxValue;
        for (var beam = 0; beam < beamScores.Length; beam++)
        {
            worst = Math.Min(worst, beamScores[beam]);
        }

        for (var beam = 0; beam < isSentFinished.Length; beam++)
        {
            var against = isSentFinished[beam] ? worst : Parked;
            if (best > against)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The two logits processors this checkpoint's generation config actually builds.
    /// </summary>
    /// <remarks>
    /// Two, and no more — checked against transformers' own <c>_get_logits_processor</c> on
    /// 2026-08-20 rather than assumed from the config file's key list. Everything else it can build
    /// is switched off by a default this checkpoint does not override: no minimum length, no
    /// repetition penalty, no n-gram blocking, no renormalisation. Adding one here would be adding
    /// a decode nobody scored; missing one would be dropping a constraint that was.
    /// </remarks>
    private static void Process(
        Span<float> logProbabilities,
        MarianConfiguration configuration,
        int beams,
        int vocabulary,
        int currentLength,
        int maxLength)
    {
        // bad_words_ids. The pad token, which is also the start token — banning it stops the search
        // emitting a token whose only job is to be the thing the decoder was primed with.
        foreach (var bad in configuration.BadWordIds)
        {
            if (bad < 0 || bad >= vocabulary)
            {
                continue;
            }

            for (var beam = 0; beam < beams; beam++)
            {
                logProbabilities[(beam * vocabulary) + bad] = float.NegativeInfinity;
            }
        }

        // forced_eos_token_id, at the last position there is. Unreachable on any real sentence at
        // 512 new tokens, and carried because "unreachable" is a claim about the corpus rather than
        // about the code.
        if (configuration.ForcedEndOfSequenceTokenId is { } forced && currentLength == maxLength - 1)
        {
            for (var beam = 0; beam < beams; beam++)
            {
                var row = logProbabilities.Slice(beam * vocabulary, vocabulary);
                for (var token = 0; token < vocabulary; token++)
                {
                    if (token != forced)
                    {
                        row[token] = float.NegativeInfinity;
                    }
                }
            }
        }
    }

    /// <summary>Log-softmax of one beam's logits.</summary>
    /// <remarks>
    /// The exponential sum accumulates in double and everything else stays in single, which is the
    /// precision the reference's score arithmetic runs in. Double here is not an attempt to be more
    /// faithful than the reference — it cannot be, its partial sums are SIMD-ordered and these are
    /// not — but to keep this side's own error far below the difference being measured, so that a
    /// disagreement is attributable rather than shared.
    /// </remarks>
    private static void LogSoftmax(ReadOnlySpan<float> logits, Span<float> into)
    {
        var maximum = float.NegativeInfinity;
        for (var i = 0; i < logits.Length; i++)
        {
            if (logits[i] > maximum)
            {
                maximum = logits[i];
            }
        }

        var total = 0.0;
        for (var i = 0; i < logits.Length; i++)
        {
            total += Math.Exp(logits[i] - maximum);
        }

        var normaliser = maximum + (float)Math.Log(total);
        for (var i = 0; i < logits.Length; i++)
        {
            into[i] = logits[i] - normaliser;
        }
    }

    /// <summary>
    /// The <paramref name="k"/> highest values, highest first, ties going to the lower index.
    /// </summary>
    /// <remarks>
    /// The tie rule is the whole reason this is written out rather than sorted: ties do happen —
    /// every beam but the first starts parked at exactly −1e9, and a parked candidate's score is
    /// exactly −1e9 again — and which of two equal candidates is kept decides which sequence
    /// continues. Lower index first is what <c>torch.topk</c> does on CPU, so it is what this does.
    /// </remarks>
    private static void TopK(ReadOnlySpan<float> values, int k, Span<int> indices, Span<float> scores)
    {
        var held = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (held == k && !(value > scores[k - 1]))
            {
                continue;
            }

            var at = held < k ? held : k - 1;
            while (at > 0 && value > scores[at - 1])
            {
                scores[at] = scores[at - 1];
                indices[at] = indices[at - 1];
                at--;
            }

            scores[at] = value;
            indices[at] = i;
            if (held < k)
            {
                held++;
            }
        }

        if (held < k)
        {
            throw new ArgumentException($"Cannot take {k} of {values.Length} values.", nameof(k));
        }
    }

    private static bool All(ReadOnlySpan<bool> values)
    {
        foreach (var value in values)
        {
            if (!value)
            {
                return false;
            }
        }

        return true;
    }
}
