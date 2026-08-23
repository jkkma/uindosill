using Parakeet.Core.Segmentation;

namespace Parakeet.Engine.SileroVad.Tests;

/// <summary>
/// The Silero detector against ONNX Runtime. Two of these need no model and run everywhere; the
/// rest need the graph, which is a download and not in the clone, so they skip themselves unless
/// <c>UINDOSILL_SILERO_VAD</c> names it — the same arrangement as the FLEURS test in Core, and for
/// the same reason: a count that depends on what is installed cannot be written into a document CI
/// checks.
/// </summary>
public sealed class SileroSpeechDetectorTests
{
    private const string ModelVariable = "UINDOSILL_SILERO_VAD";

    [Fact]
    public void AMissingModelIsRefusedByItsPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "uindosill-no-such-model", "silero_vad.onnx");

        var exception = Assert.Throws<SpeechDetectorException>(() => new SileroSpeechDetector(path));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotAGraphIsRefusedWithTheRuntimesReason()
    {
        // ONNX Runtime is the one that knows the file is not a model, so its sentence is the one a
        // reader gets — wrapped in the seam's own exception type so callers have one thing to catch.
        var path = Path.Combine(Directory.CreateTempSubdirectory("uindosill-vad").FullName, "silero_vad.onnx");
        File.WriteAllText(path, "not a graph");

        var exception = Assert.Throws<SpeechDetectorException>(() => new SileroSpeechDetector(path));

        Assert.Contains("could not load", exception.Message, StringComparison.Ordinal);
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuntimeVersionIsTheOnePinned()
    {
        // Directory.Packages.props pins 1.29.0 and says why; a report naming another version would
        // be describing a different binary.
        Assert.Equal("1.29.0", SileroSpeechDetector.RuntimeVersion);
    }

    [Fact]
    public void SilenceScoresLowAtEveryRateTheStreamIsOpenedAt()
    {
        var path = Environment.GetEnvironmentVariable(ModelVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(path),
            $"Set {ModelVariable} to a silero_vad.onnx to run the detector against the real graph.");

        using var detector = new SileroSpeechDetector(path!);
        Assert.Contains("silero-vad", detector.Name, StringComparison.Ordinal);

        foreach (var rate in new[] { 16_000, 44_100, 48_000 })
        {
            using var stream = detector.Open(rate);

            // Fed in blocks that are not multiples of the model's window, because the segmenter's
            // frames are not: 30 ms at 44.1 kHz is 1,323 samples.
            var block = new float[(int)(rate * 0.03)];
            var probability = 0f;
            for (var fed = 0; fed < rate; fed += block.Length)
            {
                probability = stream.Push(block);
            }

            Assert.InRange(probability, 0f, 0.1f);
        }
    }

    [Fact]
    public void ProbabilitiesStayInsideTheUnitIntervalOnNoise()
    {
        var path = Environment.GetEnvironmentVariable(ModelVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(path),
            $"Set {ModelVariable} to a silero_vad.onnx to run the detector against the real graph.");

        using var detector = new SileroSpeechDetector(path!);
        using var stream = detector.Open(SileroSpeechDetector.ModelSampleRate);

        var random = new Random(7);
        var block = new float[480];
        for (var fed = 0; fed < SileroSpeechDetector.ModelSampleRate * 2; fed += block.Length)
        {
            for (var i = 0; i < block.Length; i++)
            {
                block[i] = (float)((random.NextDouble() * 2 - 1) * 0.3);
            }

            var probability = stream.Push(block);
            Assert.InRange(probability, 0f, 1f);
        }
    }
}
