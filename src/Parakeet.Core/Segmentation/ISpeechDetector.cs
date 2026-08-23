namespace Parakeet.Core.Segmentation;

/// <summary>
/// A loaded speech detector: a model that scores audio for the presence of speech, which
/// <see cref="StreamingSegmenter"/> uses in place of its energy gate when it is handed one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The energy gate cannot hear a pause under a music bed. Measured on a
/// broadcast documentary on 2026-08-23: the bed under the narration sat at −23 dBFS median, above
/// the line the gate's threshold can never rise past, so segments ran to the thirty-second cap while
/// the recogniser's own word timings showed pauses of a second and more inside them
/// (<c>docs/UNPROVEN.md</c>). A detector trained on speech answers the question the gate cannot —
/// "is this speech" rather than "is this loud" — and what it costs is a second model on the
/// transcription path, which is why it is an opt-in and the gate is still the default.
/// </para>
/// <para>
/// Core declares the seam and knows nothing about the model behind it: the shipping one is a Silero
/// graph on ONNX Runtime in <c>Parakeet.Engine.SileroVad</c>, and <see cref="FakeSpeechDetector"/>
/// is the scripted stand-in the tests drive. The detector is the loaded model, shared across a batch
/// of files; <see cref="Open"/> hands out one <see cref="ISpeechDetectorStream"/> per recording,
/// which is where the per-recording state lives, so two files never share a window of context.
/// </para>
/// </remarks>
public interface ISpeechDetector : IDisposable
{
    /// <summary>What this is, for a report to name: the model, the runtime, the provider.</summary>
    string Name { get; }

    /// <summary>
    /// A stream for one recording whose samples arrive at <paramref name="sampleRate"/>. Throws
    /// <see cref="SpeechDetectorException"/> when the rate cannot be handled.
    /// </summary>
    ISpeechDetectorStream Open(int sampleRate);
}

/// <summary>
/// The per-recording half: fed the recording's samples in order, it answers with the probability
/// that the most recent of them are speech.
/// </summary>
/// <remarks>
/// Any block size, in order, at the rate the stream was opened with. The answer is the latest
/// probability the model has produced. A model works in windows of its own — Silero's is 512
/// samples at 16 kHz, 32 ms — so a decision can lag the samples that decided it by up to one window,
/// and until the first window has been seen the answer is 0. <see cref="StreamingSegmenter"/> puts
/// its own hysteresis, minimum durations and padding on top, so that lag sits well inside what it
/// already tolerates from the energy gate's own frame.
/// </remarks>
public interface ISpeechDetectorStream : IDisposable
{
    /// <summary>The detector's <see cref="ISpeechDetector.Name"/>, so a report can say what cut the audio.</summary>
    string Name { get; }

    /// <summary>Probability in [0, 1] that the audio is speech, as of the last sample pushed.</summary>
    float Push(ReadOnlySpan<float> samples);
}

/// <summary>A speech detector could not be loaded or could not take the audio it was given.</summary>
public sealed class SpeechDetectorException : Exception
{
    public SpeechDetectorException(string message)
        : base(message)
    {
    }

    public SpeechDetectorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SpeechDetectorException()
    {
    }
}
