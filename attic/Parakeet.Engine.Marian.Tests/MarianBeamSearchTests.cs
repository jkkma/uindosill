namespace Parakeet.Engine.Marian.Tests;

/// <summary>
/// The search, against scripted logits, on a machine with no weights.
/// </summary>
/// <remarks>
/// Every case here is a degree of freedom that changes what English comes out while leaving the
/// output looking entirely correct: whether the search looks past a good-looking first token, how a
/// finished hypothesis is scored against a longer one, whether a banned token can be emitted, and
/// which beam's cache carries forward. The agreement run against the recorded gate hypotheses says
/// whether the whole thing reproduces the reference; these say which part broke when it does not.
/// </remarks>
public sealed class MarianBeamSearchTests
{
    // A four-token toy vocabulary plus a start/pad and an end. Start and pad share an id, exactly
    // as they do on the real checkpoint, which is the trap worth having in the fixture too.
    private const int Vocabulary = 8;
    private const int Start = 7;
    private const int Pad = 7;
    private const int Eos = 1;
    private const int A = 2;
    private const int B = 3;
    private const int C = 4;

    private static MarianConfiguration Configuration(int? forcedEos = Eos) => new()
    {
        DecoderLayers = 1,
        DecoderAttentionHeads = 1,
        ModelDimension = 8,
        VocabularySize = Vocabulary,
        DecoderStartTokenId = Start,
        EndOfSequenceTokenId = Eos,
        PadTokenId = Pad,
        MaxPositionEmbeddings = 64,
        BadWordIds = [Pad],
        ForcedEndOfSequenceTokenId = forcedEos,
        DeclaredBeams = 4,
    };

    /// <summary>
    /// Logits that put every listed token at its score and everything else far below.
    /// </summary>
    /// <remarks>
    /// <b>Only the gaps between the listed scores mean anything.</b> The search takes a log-softmax
    /// before it does anything else, so a step listing one token gives that token a log probability
    /// of about zero however small a number it was written with — writing <c>(Eos, -5f)</c> alone
    /// does not make the end token expensive, it makes it certain. A step that is meant to cost
    /// something has to have somewhere else for the probability to go.
    /// </remarks>
    private static float[] Logits(params (int Token, float Score)[] scores)
    {
        var logits = new float[Vocabulary];
        Array.Fill(logits, -30f);
        foreach (var (token, score) in scores)
        {
            logits[token] = score;
        }

        return logits;
    }

    [Fact]
    public void ItLooksPastAGoodFirstTokenThatLeadsNowhere()
    {
        // The reason this project decodes at beam 6 rather than at one, in miniature.
        // A is the better first token and everything after it is a four-way coin toss, so every
        // sentence through A pays for the rest of itself. B costs more up front and then walks a
        // path with no competition at all.
        static float[] Script(IReadOnlyList<int> sequence) => sequence switch
        {
            [Start] => Logits((A, 0f), (B, -0.4f)),
            [Start, B] => Logits((C, 0f)),
            [Start, B, C] => Logits((Eos, 0f)),
            [Start, A, ..] => Logits((Eos, 0f), (A, 0f), (B, 0f), (C, 0f)),
            _ => Logits((Eos, 0f)),
        };

        var wide = MarianBeamSearch.Search(
            new ScriptedDecoder(Vocabulary, Script), Configuration(), [5, 6],
            new MarianDecodeSettings { Beams = 2 });

        Assert.Equal([Start, B, C, Eos], wide);

        // One beam cannot look past the first token, so it commits to A and finishes there. That
        // is the delta the 2026-08-19 spike measured on 44 real segments, in miniature.
        var narrow = MarianBeamSearch.Search(
            new ScriptedDecoder(Vocabulary, Script), Configuration(), [5, 6],
            new MarianDecodeSettings { Beams = 1 });

        Assert.Equal([Start, A, Eos], narrow);
    }

    [Fact]
    public void TheBannedTokenIsNeverEmittedEvenWhenItIsTheMostLikely()
    {
        // bad_words_ids bans the pad token, which is also the token the decoder is primed with.
        // Here it is the single most likely continuation at every step, so a loop that reads the
        // list and does not apply it produces a sequence made almost entirely of it.
        var decoder = new ScriptedDecoder(Vocabulary, sequence => sequence.Count >= 3
            ? Logits((Pad, 10f), (Eos, 0f))
            : Logits((Pad, 10f), (A, 0f)));

        var beam = MarianBeamSearch.Search(
            decoder, Configuration(), [5], new MarianDecodeSettings { Beams = 3 });

        Assert.DoesNotContain(Pad, beam.Skip(1));
        Assert.Equal([Start, A, A, Eos], beam);
    }

    [Fact]
    public void TheLengthPenaltyDecidesBetweenAShortHypothesisAndALongerOne()
    {
        // Two complete hypotheses that differ only in the one choice at the start, after which each
        // walks a path with no competition and so pays nothing more. The short one therefore has
        // the better total and the long one the better mean, and which of those decides the
        // sentence is entirely the length penalty. HuggingFace's default is 1.0 — the mean — so 1.0
        // is what every published figure for this model was produced under, which is why the value
        // is pinned in MarianDecodeSettings rather than left to a caller.
        static float[] Script(IReadOnlyList<int> sequence) => sequence switch
        {
            [Start] => Logits((A, 0f), (B, -0.4f)),
            [Start, A] => Logits((Eos, 0f)),
            [Start, B] => Logits((C, 0f)),
            [Start, B, C] => Logits((C, 0f)),
            [Start, B, C, C] => Logits((Eos, 0f)),
            _ => Logits((Eos, 0f)),
        };

        var longer = MarianBeamSearch.Search(
            new ScriptedDecoder(Vocabulary, Script), Configuration(), [5],
            new MarianDecodeSettings { Beams = 2, LengthPenalty = 1.0f });

        var shorter = MarianBeamSearch.Search(
            new ScriptedDecoder(Vocabulary, Script), Configuration(), [5],
            new MarianDecodeSettings { Beams = 2, LengthPenalty = 0.0f });

        Assert.Equal([Start, B, C, C, Eos], longer);
        Assert.Equal([Start, A, Eos], shorter);
    }

    [Fact]
    public void TheCacheIsReorderedToFollowTheBeamsThatSurvived()
    {
        // The search tells the decoder which beam each new beam continues from, and the decoder
        // permutes six layers of past keys and values to match. Get this wrong and every beam
        // decodes against another beam's history, which produces fluent output from the wrong
        // prefix — the failure mode with no symptom.
        var decoder = new ScriptedDecoder(Vocabulary, sequence => sequence switch
        {
            // Two survivors, and at the next step the better continuation is under the beam that
            // came second, so the surviving order is not the identity.
            [Start] => Logits((A, 0f), (B, -0.1f)),
            [Start, A] => Logits((C, 0f), (A, 0f), (B, 0f), (Eos, 0f)),
            [Start, B] => Logits((C, 0f)),
            _ => Logits((Eos, 0f)),
        });

        MarianBeamSearch.Search(decoder, Configuration(), [5], new MarianDecodeSettings { Beams = 2 });

        // First step: every candidate came from beam 0, because the others start parked.
        Assert.Equal([0, 0], decoder.Reorders[0]);

        // Second step: the leading beam is the one that was beam 1, so the cache has to move.
        Assert.Equal([1, 0], decoder.Reorders[1]);
    }

    [Fact]
    public void ADecodeThatWillNotStopIsEndedAtTheLimitRatherThanRunningOn()
    {
        // forced_eos_token_id, which the config carries and which is unreachable on any real
        // sentence at 512 new tokens. Here the model never proposes the end token at all, so the
        // limit is the only thing that can end it — and the sequence still ends with the end token
        // rather than being cut mid-word.
        var decoder = new ScriptedDecoder(Vocabulary, _ => Logits((A, 0f)));

        var beam = MarianBeamSearch.Search(
            decoder, Configuration(), [5], new MarianDecodeSettings { Beams = 2, MaxNewTokens = 4 });

        Assert.Equal([Start, A, A, A, Eos], beam);
    }

    [Fact]
    public void AnEmptySourceIsTheCallersProblemRatherThanAnEmptyDecode()
    {
        var decoder = new ScriptedDecoder(Vocabulary, _ => Logits((Eos, 0f)));

        Assert.Throws<ArgumentException>(() =>
            MarianBeamSearch.Search(decoder, Configuration(), [], MarianDecodeSettings.Default));
    }

    [Fact]
    public void CancellationStopsASegmentThatIsAlreadyDecoding()
    {
        // The translator declares SupportsCancellation true, and that is a claim about this: the
        // search polls between steps, so a long segment does not have to finish before a cancelled
        // run can stop.
        using var cancellation = new CancellationTokenSource();
        var decoder = new ScriptedDecoder(Vocabulary, sequence =>
        {
            if (sequence.Count >= 3)
            {
                cancellation.Cancel();
            }

            return Logits((A, 0f));
        });

        Assert.Throws<OperationCanceledException>(() => MarianBeamSearch.Search(
            decoder, Configuration(), [5], new MarianDecodeSettings { Beams = 2 }, cancellation.Token));
    }

    [Fact]
    public void TheDecodeThatWasScoredIsTheDefaultAndTheConfigFileIsNotObeyed()
    {
        // generation_config.json says num_beams 4. Nothing this project has measured used four.
        // A tripwire rather than a test of behaviour: if somebody makes the loop read the file,
        // this is what says so before a corpus is re-scored under a decode nobody ran.
        var settings = MarianDecodeSettings.Default;

        Assert.Equal(6, settings.Beams);
        Assert.Equal(512, settings.MaxNewTokens);
        Assert.Equal(1.0f, settings.LengthPenalty);
        Assert.False(settings.EarlyStopping);

        Assert.Equal(4, Configuration().DeclaredBeams);
        Assert.NotEqual(Configuration().DeclaredBeams, settings.Beams);
    }

    [Fact]
    public void ASearchWithNoBeamsIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarianDecodeSettings { Beams = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarianDecodeSettings { MaxNewTokens = 0 }.Validate());
    }
}
