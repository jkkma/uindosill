using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;

namespace Parakeet.Engine.Marian.Tests;

/// <summary>
/// The translator against its own contract.
/// </summary>
/// <remarks>
/// Split deliberately. What a translator <b>declares</b> needs no weights — the capabilities record
/// is built by the constructor, and every clause of <c>ITranscriptTranslator</c> that a caller
/// branches on is in it, so CI checks the declarations. What it <b>does</b> needs the checkpoint,
/// so those are skipped where there is none and run on a measuring machine, which is the same line
/// the diariser's tests draw.
/// </remarks>
public sealed class MarianTranslatorTests
{
    private static MarianTranscriptTranslator Unloaded() => new(new MarianTranslatorOptions
    {
        // Never loaded in the declaration tests: constructing does not touch the disk, which is
        // itself part of the contract — LoadAsync is where the expensive, refusable work happens.
        ModelDirectory = "nowhere-in-particular",
        ModelId = "opus-mt-tc-bible-big-mul-en-fp32",
        SourceLanguages = ["es", "de"],
    });

    private static TranscriptSegment Segment(int index, string text, string? speaker = null) => new()
    {
        Start = TimeSpan.FromSeconds(index * 3),
        End = TimeSpan.FromSeconds((index * 3) + 2.5),
        Text = text,
        SourceSegmentIndex = index + 100,
        Speaker = speaker,
        Words =
        [
            new TranscriptWord { Start = TimeSpan.FromSeconds(index * 3), End = TimeSpan.FromSeconds(index * 3 + 1), Text = "w" },
        ],
    };

    // ------------------------------------------------------------------ what it declares

    [Fact]
    public void EveryClauseACallerBranchesOnIsDeclaredWithoutLoadingAnything()
    {
        var capabilities = Unloaded().Capabilities;

        // The target token, which is one vocabulary entry and not three punctuation marks and a
        // word. A source without it comes back as fluent German rather than as an error.
        Assert.Equal(">>eng<<", capabilities.TargetToken);
        Assert.Equal(MarianTranscriptTranslator.EnglishTargetToken, capabilities.TargetToken);

        // Many-to-one: told the target, never the source. A translator that said true here could
        // not be driven at all, because nothing in this pipeline detects a source language.
        Assert.False(capabilities.RequiresSourceLanguage);

        // Translation reorders and rewrites, so no alignment survives and the word-timed subtitle
        // format is refused rather than written against times that no longer fit the words.
        Assert.False(capabilities.PreservesWordTimings);

        // The search polls between steps, so a segment already decoding really does stop.
        Assert.True(capabilities.SupportsCancellation);

        // Reported as ignored rather than silently dropped: see TranslatorCapabilities.
        Assert.False(capabilities.HonoursContext);

        // 512, from the tokenizer's declared model_max_length, not the 1024 in config.json.
        Assert.Equal(512, capabilities.MaxSourceTokens);

        // CPU, and priced rather than assumed: CUDA was measured at 1.2-1.5x for this model.
        Assert.Equal(ComputeBackend.Cpu, capabilities.Backend);

        Assert.Equal(["en"], capabilities.TargetLanguages);
        Assert.Equal(["es", "de"], capabilities.SourceLanguages);
        Assert.Equal("opus-mt-tc-bible-big-mul-en-fp32", capabilities.ModelId);
    }

    [Fact]
    public void ATranslatorWithNoDirectoryIsRefusedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() =>
            new MarianTranscriptTranslator(new MarianTranslatorOptions { ModelDirectory = "  " }));
    }

    [Fact]
    public async Task ADirectoryWithNoModelInItFailsOnLoadRatherThanOnTheFirstSegment()
    {
        await using var translator = Unloaded();

        // Named at load, which is where a caller can still do something about it, rather than
        // halfway through a file.
        await Assert.ThrowsAnyAsync<IOException>(async () => await translator.LoadAsync());
    }

    // ------------------------------------------------------------------ what it does

    [Fact]
    public async Task ItTranslatesRealSegmentsAndHonoursEveryClauseItDeclared()
    {
        var checkpoint = Fixtures.Checkpoint();
        Assert.SkipWhen(checkpoint is null, "Set UINDOSILL_TRANSLATION_MODEL; this one reads real weights.");

        await using var translator = new MarianTranscriptTranslator(new MarianTranslatorOptions
        {
            ModelDirectory = checkpoint!,
            ModelId = "under-test",
        });

        // Two sentences whose English was recorded by HuggingFace's own beam search over these
        // graphs on 2026-08-20, plus a blank one between them. The strings are the acceptance test:
        // "looks like a reasonable translation" is not a check, and reproducing what was scored is.
        var segments = new List<TranscriptSegment>
        {
            Segment(0, "Caracas es la capital y la ciudad más poblada de Venezuela.", "Speaker 1"),
            Segment(1, "   ", "Speaker 2"),
            Segment(
                2,
                "El elemento del determinismo cultural se encontraba muy presente en el romanticismo, " +
                "según estudiosos como Goether, Fichte y Schlegel.",
                "Speaker 1"),
        };

        var translated = new List<TranscriptSegment>();
        await foreach (var segment in translator.TranslateAsync(segments, TranslationOptions.Default))
        {
            translated.Add(segment);
        }

        Assert.Equal(
            [
                "Caracas is the capital and most populous city of Venezuela.",
                string.Empty,
                "The element of cultural determinism was very present in Romanticism, according to scholars " +
                "such as Goether, Fichte and Schlegel.",
            ],
            translated.Select(s => s.Text));

        for (var i = 0; i < segments.Count; i++)
        {
            // The timeline belongs to the audio and is not the translator's to change; the speaker
            // was decided by a pass that ran before this one; the source index is how anything
            // downstream finds its way back.
            Assert.Equal(segments[i].Start, translated[i].Start);
            Assert.Equal(segments[i].End, translated[i].End);
            Assert.Equal(segments[i].Speaker, translated[i].Speaker);
            Assert.Equal(segments[i].SourceSegmentIndex, translated[i].SourceSegmentIndex);

            // Not the source's words with new text over them.
            Assert.Empty(translated[i].Words);
        }
    }

    [Fact]
    public async Task TheDriverAcceptsWhatThisTranslatorReturns()
    {
        var checkpoint = Fixtures.Checkpoint();
        Assert.SkipWhen(checkpoint is null, "Set UINDOSILL_TRANSLATION_MODEL; this one reads real weights.");

        // Through TranscriptTranslation rather than around it: the driver re-checks every clause
        // above on every segment, so this is the contract enforced by the code that enforces it
        // rather than by this test's own reading of it.
        await using var translator = new MarianTranscriptTranslator(new MarianTranslatorOptions
        {
            ModelDirectory = checkpoint!,
            ModelId = "under-test",
        });

        var document = new TranscriptDocument
        {
            Segments = [Segment(0, "Caracas es la capital de Venezuela."), Segment(1, "Buenos días.")],
        };

        var english = await TranscriptTranslation.TranslateAsync(document, translator);

        Assert.Equal("en", english.TranslatedTo);
        Assert.Equal("under-test", english.TranslationModelId);
        Assert.Equal(document.Segments.Count, english.Segments.Count);
        Assert.All(english.Segments, segment => Assert.NotEmpty(segment.Text));

        // The source document is the caller's to keep.
        Assert.Equal("Caracas es la capital de Venezuela.", document.Segments[0].Text);
        Assert.Null(document.TranslatedTo);
    }

    [Fact]
    public async Task ASourcePastTheLimitIsRefusedRatherThanTruncated()
    {
        var checkpoint = Fixtures.Checkpoint();
        Assert.SkipWhen(checkpoint is null, "Set UINDOSILL_TRANSLATION_MODEL; this one reads real weights.");

        await using var translator = new MarianTranscriptTranslator(new MarianTranslatorOptions
        {
            ModelDirectory = checkpoint!,
            ModelId = "under-test",
        });

        // Far past 512 tokens. Half a sentence translated fluently, with nothing to say the rest
        // was dropped, is the failure the whole contract exists to avoid — so this throws, and the
        // exception names the segment and both numbers.
        var long_ = string.Join(' ', Enumerable.Repeat("Caracas es la capital y la ciudad más poblada de Venezuela.", 60));

        var exception = await Assert.ThrowsAsync<SegmentTooLongException>(async () =>
        {
            await foreach (var _ in translator.TranslateAsync([Segment(0, long_)], TranslationOptions.Default))
            {
            }
        });

        Assert.Equal(0, exception.SegmentIndex);
        Assert.Equal(512, exception.Limit);
        Assert.True(exception.Tokens > 512, $"the source measured {exception.Tokens} tokens, which is not over the limit");
    }

    [Fact]
    public async Task LoadingTwiceLoadsOnce()
    {
        var checkpoint = Fixtures.Checkpoint();
        Assert.SkipWhen(checkpoint is null, "Set UINDOSILL_TRANSLATION_MODEL; this one reads real weights.");

        await using var translator = new MarianTranscriptTranslator(new MarianTranslatorOptions
        {
            ModelDirectory = checkpoint!,
        });

        // Idempotent and expensive: 1.34 GiB of graphs, and a second call must not open a second
        // pair of sessions.
        await translator.LoadAsync();
        await translator.LoadAsync();

        // The limit is refined from the tokenizer at load, and agrees with what was declared before.
        Assert.Equal(512, translator.Capabilities.MaxSourceTokens);
    }
}
