using System.Text.Json;
using Parakeet.Audio;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.Cli.Tests;

/// <summary>
/// Drives <c>transcribe --translate</c> through the real entry point, against the canned engine and
/// the canned translator. These mirror the seven that hold up the speaker opt-in, because the two
/// opt-ins have the same shape and the same failure modes: a flag that is off by default, a model
/// this build has not got, a refusal that has to name the right missing thing, and output that must
/// be byte-identical when the flag is absent.
/// </summary>
public class TranslateCommandTests
{
    private sealed class Harness : IDisposable
    {
        public Harness(ModelCatalog? catalog = null)
        {
            Directory = TestTemp.NewDirectory("uindosill-translate");
            Out = new StringWriter();
            Error = new StringWriter();
            Context = new CliContext
            {
                Out = Out,
                Error = Error,
                Store = new LocalModelStore(Path.Combine(Directory, "models")),
                Catalog = catalog ?? ModelCatalog.Default,
                Interactive = false,
            };
        }

        public string Directory { get; }

        public StringWriter Out { get; }

        public StringWriter Error { get; }

        public CliContext Context { get; }

        public string Write(string name, string content)
        {
            var path = Path.Combine(Directory, name);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public string WriteWav(string name, double seconds)
        {
            var path = Path.Combine(Directory, name);
            var rate = 16_000;
            var samples = new float[(int)(seconds * rate)];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 200 * i / rate));
            }

            WavWriter.WriteFile(path, samples, rate);
            return path;
        }

        public string Path_(string name) => Path.Combine(Directory, name);

        public Task<int> RunAsync(params string[] args) =>
            CliEntryPoint.RunAsync(args, Context, CancellationToken.None);

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task TranslateWritesTheEnglishTranscriptUnderItsOwnName()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 9);

        var exit = await harness.RunAsync("transcribe", "--fake", "--translate", "-f", "txt,srt,vtt,json,md", input);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("canned translator", harness.Error.ToString(), StringComparison.Ordinal);

        // The .en infix, on every format. It is what stops a translated run overwriting a plain
        // one under --overwrite, and for SubRip and plain text it is the only place the output can
        // say it is not the language that was spoken.
        Assert.False(File.Exists(harness.Path_("call.txt")));
        Assert.True(File.Exists(harness.Path_("call.en.txt")));
        Assert.True(File.Exists(harness.Path_("call.en.srt")));

        var txt = await File.ReadAllTextAsync(harness.Path_("call.en.txt"));
        Assert.Contains("[en] ", txt, StringComparison.Ordinal);

        var vtt = await File.ReadAllTextAsync(harness.Path_("call.en.vtt"));
        Assert.Contains("NOTE Translated into en by fake-translator.", vtt, StringComparison.Ordinal);

        var md = await File.ReadAllTextAsync(harness.Path_("call.en.md"));
        Assert.Contains("| Translated into | en |", md, StringComparison.Ordinal);
        Assert.Contains("| Translation model | fake-translator |", md, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(harness.Path_("call.en.json")));
        Assert.Equal("en", json.RootElement.GetProperty("translatedTo").GetString());
        Assert.Equal("fake-translator", json.RootElement.GetProperty("translationModel").GetString());

        // Word timings do not survive translation, so the segments carry none rather than the ones
        // that belonged to the speech.
        Assert.False(json.RootElement.GetProperty("segments")[0].TryGetProperty("words", out _));

        // The ASR provenance survives beside the translator's: two models made this file.
        Assert.Equal("fake-model", json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task WithoutTheFlagNothingAboutTranslationAppears()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 6);

        var exit = await harness.RunAsync("transcribe", "--fake", "-f", "txt,json,vtt", input);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.False(File.Exists(harness.Path_("call.en.txt")));
        Assert.DoesNotContain("[en]", await File.ReadAllTextAsync(harness.Path_("call.txt")), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "translat", await File.ReadAllTextAsync(harness.Path_("call.json")), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            "WEBVTT\n\n1\n", await File.ReadAllTextAsync(harness.Path_("call.vtt")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskingForWordTimedVttWithTranslateIsRefusedRatherThanDegraded()
    {
        // Every other subtitle format falls back to spacing a cue across its segment, which it
        // already does for any segment the engine returned no word timings for. This one cannot:
        // its whole content is a time per word, and the English words are not the words that were
        // spoken. Refused before anything is written, not written and then explained.
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 5);

        var exit = await harness.RunAsync("transcribe", "--fake", "--translate", "-f", "txt,vtt-words", input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("does not carry word timings", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(harness.Path_("call.en.words.vtt")));
        Assert.False(File.Exists(harness.Path_("call.en.txt")));

        // And without --translate the same format is written as it always was.
        Assert.Equal(ExitCodes.Success, await harness.RunAsync("transcribe", "--fake", "-f", "vtt-words", input));
        Assert.True(File.Exists(harness.Path_("call.words.vtt")));
    }

    [Fact]
    public async Task TranslateWithoutTheModelSaysSoAndStopsBeforeTranscribing()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 3);
        harness.Write("models/tdt-0.6b-v3-f16.gguf", "not a model");   // so the engine would resolve

        var exit = await harness.RunAsync(
            "transcribe", "--translate", "--model-path", harness.Path_("models/tdt-0.6b-v3-f16.gguf"), input);

        // The catalogue has a translation entry as of 2026-08-20, so the refusal changed from
        // "this build has no translator" to "the one it has is not installed" — and it still
        // arrives before any audio is read, which is the part that matters.
        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("is not installed", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("opus-mt-tc-bible-big-mul-en-fp32", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(harness.Path_("call.en.txt")));
        Assert.False(File.Exists(harness.Path_("call.txt")));
    }

    [Fact]
    public async Task TheRefusalNamesTranslationEvenWhenTheAsrModelIsAlsoMissing()
    {
        // Both are missing here, and only one of them is what --translate asked for. "Download the
        // ASR model you have not got" is the wrong answer, and it is the answer that would come
        // back if the engine were the first thing to fail.
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 3);

        var exit = await harness.RunAsync("transcribe", "--translate", input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("is not installed", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("opus-mt-tc-bible-big-mul-en-fp32", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("tdt-0.6b-v3", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateWithFakeNeedsNothingInstalled()
    {
        // The canned translator is what keeps the whole opt-in — the marking, the pass, the .en
        // naming, the refusals — exercisable on a machine with no weights at all. Adding a real
        // translator must not quietly make --fake depend on one.
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 3);

        var exit = await harness.RunAsync("transcribe", "--fake", "--translate", input);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(harness.Path_("call.en.txt")));
    }

    [Fact]
    public async Task ContextSegmentsNeedsTranslateAndANonNegativeNumber()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 6);

        Assert.Equal(
            ExitCodes.UsageError,
            await harness.RunAsync("transcribe", "--fake", "--context-segments", "2", input));
        Assert.Contains("only means something with --translate", harness.Error.ToString(), StringComparison.Ordinal);

        Assert.Equal(
            ExitCodes.UsageError,
            await harness.RunAsync("transcribe", "--fake", "--translate", "--context-segments", "-1", input));

        // Zero is legal and is the default: each segment translated on its own.
        Assert.Equal(
            ExitCodes.Success,
            await harness.RunAsync("transcribe", "--fake", "--translate", "--context-segments", "2", input));
        Assert.True(File.Exists(harness.Path_("call.en.txt")));
    }

    [Fact]
    public async Task SpeakersSurviveTheTranslationPassThatRunsAfterThem()
    {
        // The order the code forces: decode, label, translate. The names are still in front of the
        // lines afterwards, and the RTTM the labeller produced is untouched by a pass that never
        // saw the audio.
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 9);

        var exit = await harness.RunAsync(
            "transcribe", "--fake", "--speakers", "--translate", "-f", "txt,rttm", input);

        Assert.Equal(ExitCodes.Success, exit);

        var txt = await File.ReadAllTextAsync(harness.Path_("call.en.txt"));
        Assert.Contains("] Speaker 1: [en] ", txt, StringComparison.Ordinal);
        Assert.Contains("] Speaker 2: [en] ", txt, StringComparison.Ordinal);

        var rttm = await File.ReadAllTextAsync(harness.Path_("call.en.rttm"));
        Assert.StartsWith("SPEAKER call 1 0.000 4.000 <NA> <NA> Speaker_1 <NA> <NA>\n", rttm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLanguageHintIsNotMistakenForATranslationTarget()
    {
        // The two flags are one letter apart in a help listing and a mile apart in what they do.
        // --language is a hint to the speech model about the audio; the translator is many-to-one
        // and is never told what it is reading.
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 3);

        var exit = await harness.RunAsync("transcribe", "--fake", "--translate", "--language", "es", input);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("not a translation target", harness.Error.ToString(), StringComparison.Ordinal);

        harness.Error.GetStringBuilder().Clear();
        await harness.RunAsync("transcribe", "--fake", "--overwrite", "--translate", input);
        Assert.DoesNotContain("not a translation target", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATranslatedRunDoesNotOverwriteAPlainOne()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 6);

        Assert.Equal(ExitCodes.Success, await harness.RunAsync("transcribe", "--fake", "--overwrite", input));
        var plain = await File.ReadAllTextAsync(harness.Path_("call.txt"));

        Assert.Equal(
            ExitCodes.Success,
            await harness.RunAsync("transcribe", "--fake", "--overwrite", "--translate", input));

        Assert.Equal(plain, await File.ReadAllTextAsync(harness.Path_("call.txt")));
        Assert.NotEqual(plain, await File.ReadAllTextAsync(harness.Path_("call.en.txt")));
    }

    private const string CatalogueWithATranslator = """
        {
          "schema": 1,
          "models": [
            {
              "id": "asr-a", "family": "f", "displayName": "ASR A", "quantisation": "f16", "fileName": "a.gguf",
              "url": "https://example.test/a.gguf", "sizeBytes": 10, "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
              "verified": true, "license": "CC-BY-4.0", "attributionId": "nvidia-parakeet-tdt-0.6b-v3", "recommended": false
            },
            {
              "id": "mt-x", "task": "translation", "family": "opus-mt", "displayName": "OPUS-MT mul-en int8", "quantisation": "int8", "fileName": "t.onnx",
              "url": "https://example.test/t.onnx", "sizeBytes": 10, "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
              "verified": true, "license": "CC-BY-4.0", "attributionId": "nvidia-parakeet-tdt-0.6b-v3", "recommended": true
            }
          ]
        }
        """;

    [Fact]
    public async Task ATranslationEntryIsNeverSelectableAsAnAsrModelOrADiariser()
    {
        using var harness = new Harness(ModelCatalog.Parse(CatalogueWithATranslator));
        var input = harness.WriteWav("call.wav", 3);
        harness.Write("models/a.gguf", "x");
        harness.Write("models/t.onnx", "x");

        // Explicitly, as the ASR model: refused with the reason.
        Assert.Equal(ExitCodes.UsageError, await harness.RunAsync("transcribe", "--model", "mt-x", input));
        Assert.Contains(
            "translation model, not a transcription model", harness.Error.ToString(), StringComparison.Ordinal);

        // Explicitly, as the diariser: refused with its own reason, because "not an ASR model" is
        // not the answer to "--speaker-model".
        harness.Error.GetStringBuilder().Clear();
        Assert.Equal(
            ExitCodes.UsageError,
            await harness.RunAsync("transcribe", "--speakers", "--speaker-model", "mt-x", input));
        Assert.Contains(
            "translation model, not a diarisation model", harness.Error.ToString(), StringComparison.Ordinal);

        // Implicitly, as the recommended entry: the recommended flag on it does not reach transcribe.
        Assert.Equal("asr-a", harness.Context.Catalog.Recommended?.Id);
        Assert.Equal(["mt-x"], harness.Context.Catalog.TranslationModels.Select(m => m.Id));
        Assert.Empty(harness.Context.Catalog.DiarisationModels);

        // And the listing says which is which.
        await harness.RunAsync("models", "list");
        Assert.Contains(
            "translation model — not selectable for transcribe", harness.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnomalyReportReadsTheTranscriptRatherThanItsTranslation()
    {
        // Both signals it rests on are destroyed by translation: a translated segment carries no
        // word confidences, and a stretch the model emitted in Cyrillic comes back as English
        // prose. So the warning is computed from the document the engine produced, before the pass
        // runs — and this is the test of why, because no CLI invocation can reach the difference.
        // The canned engine writes Latin at confidences well above the threshold, and the threshold
        // is not a flag, so both documents are handed to it directly.
        var options = TranscriptionOptions.Default;

        var transcribed = new TranscriptDocument
        {
            Segments =
            [
                Segment(0, "the quick brown fox", 0.9f),
                Segment(3, "продолжение следует", 0.2f),
            ],
        };

        // What the same document looks like after the pass: English text in place of what was
        // written, and no words at all. Both of the anomaly's inputs are gone — the Cyrillic with
        // the text it was in, the confidences with the words that carried them.
        var translated = transcribed with
        {
            Segments =
            [
                transcribed.Segments[0] with { Text = "the quick brown fox", Words = [] },
                transcribed.Segments[1] with { Text = "to be continued", Words = [] },
            ],
            TranslatedTo = "en",
            TranslationModelId = "fake-translator",
        };

        var reported = TranscribeCommand.DescribeAnomalies(transcribed, options);
        Assert.NotNull(reported);
        Assert.Contains("changed script", reported, StringComparison.Ordinal);
        Assert.Contains("below", reported, StringComparison.Ordinal);

        // Read off the translation instead, and both halves of the warning disappear silently.
        Assert.Null(TranscribeCommand.DescribeAnomalies(translated, options));

        static TranscriptSegment Segment(double start, string text, float confidence) => new()
        {
            Start = TimeSpan.FromSeconds(start),
            End = TimeSpan.FromSeconds(start + 3),
            Text = text,
            Words = [.. text.Split(' ').Select((w, i) => new TranscriptWord
            {
                Text = w,
                Start = TimeSpan.FromSeconds(start + i),
                End = TimeSpan.FromSeconds(start + i + 1),
                Confidence = confidence,
            })],
        };
    }

    [Fact]
    public async Task TheFlagIsInTheHelpAndSaysWhatSeparatesItFromTheLanguageHint()
    {
        using var harness = new Harness();

        Assert.Equal(ExitCodes.Success, await harness.RunAsync("transcribe", "--help"));

        var help = harness.Out.ToString();
        Assert.Contains("--translate", help, StringComparison.Ordinal);
        Assert.Contains("NOT --language", help, StringComparison.Ordinal);
        Assert.Contains(".en infix", help, StringComparison.Ordinal);
        Assert.Contains("decode, then label speakers, then translate", help, StringComparison.Ordinal);
    }
}
