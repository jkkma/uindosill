using Parakeet.Core.Answers;
using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

public class AnswerEngineTests
{
    private static TranscriptDocument Transcript(params string[] texts)
    {
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < texts.Length; i++)
        {
            segments.Add(new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(i * 10),
                End = TimeSpan.FromSeconds((i * 10) + 10),
                Text = texts[i],
            });
        }

        return new TranscriptDocument
        {
            Segments = segments,
            AudioDuration = TimeSpan.FromSeconds(texts.Length * 10),
        };
    }

    private static async Task<string> Collect(
        IAnswerEngine engine, AskRequest request, IProgress<AskProgress>? progress = null)
    {
        var chunks = new List<string>();
        await foreach (var chunk in engine.AskAsync(request, progress))
        {
            chunks.Add(chunk);
        }

        return string.Concat(chunks);
    }

    [Fact]
    public async Task WhatTheFakeSaysPassesTheValidatorAgainstTheSameTranscript()
    {
        // The property the whole seam promises: an engine's stream, parsed by the one parser
        // and checked by the one validator, resolves against the transcript it was asked about.
        var transcript = Transcript(
            "we opened with the forecast",
            "then the budget took an hour",
            "and the closing thanks were brief");
        var windows = TranscriptWindowBuilder.Build(
            transcript, new TranscriptWindowOptions { WindowLength = TimeSpan.FromSeconds(10), Stride = TimeSpan.FromSeconds(10) });

        await using var engine = new FakeAnswerEngine();
        await engine.LoadAsync();

        var text = await Collect(engine, new AskRequest
        {
            Question = "what happened?",
            Transcript = transcript,
            Evidence = [windows[0], windows[2]],
        });

        // Parsed as the app parses it: every mode asks for an opening sentence (2026-08-25),
        // so every mode reads one back — the fake included.
        var answer = AnswerParser.Parse(text, allowLead: true);
        Assert.False(answer.Abstained);
        Assert.NotNull(answer.Lead);
        Assert.Equal(3, answer.Bullets.Count);

        var validation = CitationValidator.Validate(answer, transcript);
        Assert.True(validation.AllCitationsPass);
        Assert.NotNull(validation.Lead);

        // The quote came from the cited span, so the strictest check the validator has is
        // exercised end to end — QuoteMatches, not merely null.
        var quoted = validation.Bullets[0].Citations[0];
        Assert.True(quoted.Check.QuoteMatches);

        // And the admitted-uncited case is present for a renderer to show.
        Assert.True(answer.Bullets[^1].IsUncited);
    }

    [Fact]
    public async Task EmptyEvidenceAbstainsInEveryModeAndSoDoesAnEmptyTranscript()
    {
        // The model is never asked to answer from nothing, and no engine fills the evidence in
        // itself: a fake that built its own windows in whole-transcript mode would be more
        // forgiving than the real engine, which is how two v1 defects got through.
        await using var engine = new FakeAnswerEngine();
        await engine.LoadAsync();

        var noEvidence = await Collect(engine, new AskRequest
        {
            Question = "anything?",
            Transcript = Transcript("plenty of speech"),
            Mode = AnswerMode.Retrieval,
        });
        Assert.True(AnswerParser.Parse(noEvidence).Abstained);

        var noEvidenceWhole = await Collect(engine, new AskRequest
        {
            Question = "main topics?",
            Transcript = Transcript("plenty of speech"),
            Mode = AnswerMode.WholeTranscript,
        });
        Assert.True(AnswerParser.Parse(noEvidenceWhole).Abstained);

        var noSpeech = await Collect(engine, new AskRequest
        {
            Question = "anything?",
            Transcript = TranscriptDocument.Empty,
            Mode = AnswerMode.WholeTranscript,
        });
        Assert.True(AnswerParser.Parse(noSpeech).Abstained);
    }

    [Fact]
    public async Task WholeTranscriptModeCitesTheCoverWindowsItWasHanded()
    {
        var transcript = Transcript("the one thing that was said", "and the other thing");

        await using var engine = new FakeAnswerEngine();
        await engine.LoadAsync();

        var answer = AnswerParser.Parse(await Collect(engine, new AskRequest
        {
            Question = "main topics?",
            Transcript = transcript,
            Mode = AnswerMode.WholeTranscript,
            Evidence = TranscriptWindowBuilder.Build(transcript, TranscriptWindowOptions.Cover),
        }));

        Assert.False(answer.Abstained);
        Assert.Contains(answer.Bullets, b => !b.IsUncited);
        Assert.True(CitationValidator.Validate(answer, transcript).AllCitationsPass);
    }

    [Fact]
    public async Task PrefillCompletesBeforeTheFirstChunkArrives()
    {
        // The wait a real engine imposes is the prefill; a panel renders it from these reports,
        // and a fake that skipped them would let the panel ship with no progress state.
        var transcript = Transcript("some words to count");
        var windows = TranscriptWindowBuilder.Build(transcript);

        await using var engine = new FakeAnswerEngine();
        await engine.LoadAsync();

        var reports = new List<AskProgress>();
        var progress = new SynchronousProgress(reports.Add);
        await Collect(engine, new AskRequest
        {
            Question = "?",
            Transcript = transcript,
            Evidence = windows,
        }, progress);

        Assert.True(reports.Count >= 3);
        Assert.Equal(0, reports[0].PrefillTokens);
        Assert.Equal(1, reports[1].PrefillFraction);
        Assert.Equal(0, reports[1].GeneratedTokens);
        Assert.True(reports[^1].GeneratedTokens > 0);
    }

    [Fact]
    public async Task EvidenceFromSomeOtherTranscriptIsRefused()
    {
        // The v1 lesson, applied: a fake more forgiving than the device lets real defects
        // through. Ids are only meaningful against one transcript; evidence from another is
        // exactly the mismatch the citation rule exists to catch.
        var big = Transcript("a", "b", "c", "d", "e", "f", "g", "h");
        var small = Transcript("only", "two");
        var foreign = TranscriptWindowBuilder.FromRun(big, 5, 8);

        await using var engine = new FakeAnswerEngine();
        await engine.LoadAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => Collect(engine, new AskRequest
        {
            Question = "?",
            Transcript = small,
            Evidence = [foreign],
        }));
    }

    [Fact]
    public async Task AskingBeforeLoadingRefuses()
    {
        await using var engine = new FakeAnswerEngine();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(engine, new AskRequest
        {
            Question = "?",
            Transcript = Transcript("words"),
        }));
    }

    [Fact]
    public async Task TheFailureKnobsFail()
    {
        await using var broken = new FakeAnswerEngine(new FakeAnswerOptions { FailOnLoad = true });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await broken.LoadAsync());

        var transcript = Transcript("something worth citing here");
        await using var dying = new FakeAnswerEngine(new FakeAnswerOptions { FailAfterChunks = 1 });
        await dying.LoadAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(dying, new AskRequest
        {
            Question = "?",
            Transcript = transcript,
            Evidence = TranscriptWindowBuilder.Build(transcript),
        }));
    }

    [Fact]
    public async Task CancellationStopsTheStream()
    {
        var transcript = Transcript("something worth citing here");
        await using var engine = new FakeAnswerEngine(new FakeAnswerOptions
        {
            PerChunkDelay = TimeSpan.FromMilliseconds(1),
        });
        await engine.LoadAsync();

        using var cts = new CancellationTokenSource();
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in engine.AskAsync(
                new AskRequest
                {
                    Question = "?",
                    Transcript = transcript,
                    Evidence = TranscriptWindowBuilder.Build(transcript),
                },
                ct: cts.Token))
            {
                seen++;
                cts.Cancel();
            }
        });

        Assert.Equal(1, seen);
    }

    /// <summary>
    /// <see cref="Progress{T}"/> posts to a captured sync context and a test without one gets
    /// its reports late or never; this delivers them on the spot, in order.
    /// </summary>
    private sealed class SynchronousProgress(Action<AskProgress> handler) : IProgress<AskProgress>
    {
        public void Report(AskProgress value) => handler(value);
    }
}
