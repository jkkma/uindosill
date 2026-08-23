using NAudio.CoreAudioApi;
using NAudio.Wave;
using Parakeet.Audio;

namespace Parakeet.App.Services;

/// <summary>Thrown when a file cannot be played, carrying the reason a person can act on.</summary>
public sealed class AudioPlaybackException : Exception
{
    public AudioPlaybackException(string message)
        : base(message)
    {
    }

    public AudioPlaybackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AudioPlaybackException()
    {
    }
}

/// <summary>
/// Plays one file at a time, with a position that can be read and moved.
/// </summary>
/// <remarks>
/// <para>
/// The application had no playback at all before the Ask tab: <c>Parakeet.Audio</c> decodes for
/// transcription and never sounds anything, and <c>scripts/preview-words-vtt.html</c> reads a
/// player's clock without ever setting it. So this is new surface rather than a wrapper over
/// something, and it is deliberately the smallest surface that a transcript-you-can-click needs —
/// open, play, pause, seek, and a position to read. No volume, no rate, no playlist.
/// </para>
/// <para>
/// There is no clock in here and no event that fires as the position moves. The caller reads
/// <see cref="Position"/> when it wants to draw, which keeps every thread question on the caller's
/// side of the interface and makes the whole thing testable by advancing a fake by hand.
/// </para>
/// </remarks>
public interface IAudioPlayer : IDisposable
{
    /// <summary>The file that is open, or null when none is.</summary>
    string? Path { get; }

    /// <summary>How long the open recording is, or zero when nothing is open.</summary>
    TimeSpan Duration { get; }

    /// <summary>Where in the recording playback has reached.</summary>
    TimeSpan Position { get; }

    /// <summary>Whether sound is coming out right now.</summary>
    bool IsPlaying { get; }

    /// <summary>
    /// Opens <paramref name="path"/>, replacing whatever was open, stopped and at zero.
    /// </summary>
    /// <exception cref="AudioPlaybackException">
    /// The file cannot be played here, and the message says why — an unreadable container, no
    /// output device, or a platform with no audio stack this build can reach.
    /// </exception>
    void Open(string path);

    /// <summary>Closes whatever is open. Doing it twice is not an error.</summary>
    void Close();

    /// <summary>Starts, or resumes. From the end of the recording, starts again from the top.</summary>
    void Play();

    /// <summary>Stops without moving the position.</summary>
    void Pause();

    /// <summary>Moves the position, clamped to the recording, without changing whether it is playing.</summary>
    void Seek(TimeSpan position);
}

/// <summary>
/// The real one: Media Foundation or the managed WAVE reader for the file, WASAPI for the device.
/// </summary>
/// <remarks>
/// <para>
/// Both halves are already in this application's dependency graph — <c>NAudio.Wasapi</c> and
/// <c>NAudio.Core</c> arrive through <c>Parakeet.Audio</c>, which uses the first of them to decode
/// for transcription — so playback costs no new package and no second native stack.
/// </para>
/// <para>
/// Which reader opens a file follows <c>AudioSources.Open</c> rather than being decided again here:
/// WAVE goes to the managed reader on every platform because it handles RF64 and odd bit depths
/// predictably, and everything else needs Media Foundation and therefore Windows. Sniffed from the
/// magic bytes, not the extension, for the same reason that method does it.
/// </para>
/// <para>
/// The platform check is at run time rather than compiled away, which is the lesson
/// <c>Parakeet.Audio.csproj</c>'s own header records: a Windows path behind a compile-time switch
/// on a target framework nothing references is a path that ships unreachable in every build.
/// </para>
/// <para>
/// <b>Nothing in the suite exercises this class.</b> It needs a Windows audio endpoint, which CI
/// has not got and a headless run has not got either, so the tests drive
/// <see cref="FakeAudioPlayer"/> and this is covered by running the application. See
/// <c>docs/UNPROVEN.md</c>.
/// </para>
/// </remarks>
public sealed class SystemAudioPlayer : IAudioPlayer
{
    /// <summary>
    /// The shared-mode buffer, in milliseconds. Large enough that a stall in the UI thread cannot
    /// starve the render thread, small enough that a seek is heard as one.
    /// </summary>
    private const int LatencyMilliseconds = 200;

    /// <summary>
    /// How near the end counts as being at it, when deciding whether play should start over.
    /// </summary>
    /// <remarks>
    /// See <see cref="Play"/>. A seek to the end lands on a frame boundary rather than on the
    /// duration, and which side of it that falls on depends on the container.
    /// </remarks>
    private static readonly TimeSpan EndOfStreamSlack = TimeSpan.FromMilliseconds(1);

    private WaveStream? _reader;
    private WasapiOut? _output;
    private bool _disposed;

    public string? Path { get; private set; }

    public TimeSpan Duration { get; private set; }

    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Close();

        if (!File.Exists(path))
        {
            throw new AudioPlaybackException($"'{System.IO.Path.GetFileName(path)}' is no longer where it was.");
        }

        _reader = OpenReader(path);
        Duration = _reader.TotalTime;
        Path = path;
    }

    public void Close()
    {
        // Output before reader: the render thread reads through the one to fill the other, and
        // disposing the reader out from under it is a read on a disposed stream.
        _output?.Dispose();
        _output = null;

        _reader?.Dispose();
        _reader = null;

        Duration = TimeSpan.Zero;
        Path = null;
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_reader is null)
        {
            return;
        }

        // A device that has run to the end of the stream is thrown away rather than restarted.
        // WASAPI's client has been stopped by then, and re-driving one from that state is a
        // question about NAudio's internals; making a new one is a question about nothing. It
        // costs a device activation on replay and nothing at all on pause-then-resume, which is
        // the case that has to feel instant.
        if (_output is { PlaybackState: PlaybackState.Stopped })
        {
            _output.Dispose();
            _output = null;
        }

        // Wrapping round is what a play button does at the end of a recording. Without it the
        // button is live, does nothing audible, and looks broken.
        //
        // Two things here were found on 2026-08-22 by driving this class against a real device,
        // and neither was visible from the suite.
        //
        // It is outside the branch below. Inside it, the wrap only happened when there was no
        // output to reuse — which is the case after a recording has run out on its own, so the
        // common path looked right. Drag the bar to the end, or pause there, and the device is
        // *paused* rather than stopped: play resumed a reader with nothing left to read and the
        // button did nothing at all. The fake player had always wrapped on every play, so the
        // suite was green over it.
        //
        // And the comparison has a tolerance, because an exact one is a coin toss at the boundary.
        // Seeking to the very end lands on a frame boundary rather than on the duration: measured
        // over three files, an mp3 and a WAVE landed exactly on it and an m4a landed 0.006 ms
        // short, so `>=` wrapped two of the three and left the third playing a reader with nothing
        // in it. The bound is one millisecond — 48 frames at 48 kHz, inaudible, an order of
        // magnitude under a single frame's worth of slack at any rate this opens, and 160 times
        // the largest gap seen.
        if (_reader.TotalTime - _reader.CurrentTime <= EndOfStreamSlack)
        {
            _reader.CurrentTime = TimeSpan.Zero;
        }

        _output ??= CreateOutput(_reader);
        _output.Play();
    }

    public void Pause() => _output?.Pause();

    public void Seek(TimeSpan position)
    {
        if (_reader is null)
        {
            return;
        }

        var target = position < TimeSpan.Zero ? TimeSpan.Zero
            : position > _reader.TotalTime ? _reader.TotalTime
            : position;

        // Paused around the move, and put back as it was. The render thread pulls from this same
        // reader, and a seek that lands between its read of the position and its read of the bytes
        // is a burst of the wrong audio. Pausing first is cheap and removes the question.
        var wasPlaying = IsPlaying;

        if (wasPlaying)
        {
            _output!.Pause();
        }

        _reader.CurrentTime = target;

        if (wasPlaying)
        {
            _output!.Play();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
    }

    private static WaveStream OpenReader(string path)
    {
        var name = System.IO.Path.GetFileName(path);

        AudioFormatDetection detection;

        try
        {
            detection = AudioFormatSniffer.DetectFile(path);
        }
        catch (IOException ex)
        {
            throw new AudioPlaybackException($"Could not read '{name}': {ex.Message}", ex);
        }

        try
        {
            if (detection.Container == AudioContainer.Wav)
            {
                // FileShare.Read explicitly, so playing a file that is being transcribed does not
                // take the transcription's own read away from it. Both readers in Parakeet.Audio
                // share the same way.
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

                try
                {
                    return new WaveFileReader(stream);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }

            if (!OperatingSystem.IsWindows())
            {
                throw new AudioPlaybackException(
                    $"'{name}' needs Media Foundation to decode, which exists only on Windows. " +
                    "WAVE files play on any platform this build runs on.");
            }

            return new MediaFoundationReader(path);
        }
        catch (Exception ex) when (ex is not AudioPlaybackException and (IOException or FormatException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException))
        {
            throw new AudioPlaybackException(
                $"Could not play '{name}' ({detection.Container}). This machine has no decoder for it. " +
                $"Underlying error: {ex.Message}",
                ex);
        }
    }

    private static WasapiOut CreateOutput(WaveStream reader)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new AudioPlaybackException(
                "This build plays sound through WASAPI, which exists only on Windows. " +
                "The transcript is readable here; the recording is not playable.");
        }

        WasapiOut? output = null;

        try
        {
            output = new WasapiOut(AudioClientShareMode.Shared, LatencyMilliseconds);
            output.Init(reader);
            return output;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            output?.Dispose();

            throw new AudioPlaybackException(
                "Windows would not open an audio output device. " +
                $"Underlying error: {ex.Message}",
                ex);
        }
    }
}

/// <summary>
/// A player with a clock and no sound, whose position moves only when it is told to.
/// </summary>
/// <remarks>
/// What the tests drive, for the reason <see cref="SystemAudioPlayer"/> gives: WASAPI needs an
/// output device, and neither CI nor a headless run has one. Everything the window does with a
/// player — open, play, seek from a transcript cue, follow the position, stop at the end — is
/// exercised through this, so what is untested is the device rather than the behaviour.
/// </remarks>
public sealed class FakeAudioPlayer : IAudioPlayer
{
    /// <summary>How long anything it opens turns out to be.</summary>
    public TimeSpan DurationToReport { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// When set, <see cref="Open"/> refuses with this message instead of opening — which is how a
    /// test drives the window's unplayable-file path without needing an unplayable file.
    /// </summary>
    public string? RefuseWith { get; set; }

    public string? Path { get; private set; }

    public TimeSpan Duration { get; private set; }

    public TimeSpan Position { get; private set; }

    public bool IsPlaying { get; private set; }

    /// <summary>How many times a file has been opened, so a test can see a re-open it did not want.</summary>
    public int Opens { get; private set; }

    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (RefuseWith is { Length: > 0 } reason)
        {
            Close();
            throw new AudioPlaybackException(reason);
        }

        Opens++;
        Path = path;
        Duration = DurationToReport;
        Position = TimeSpan.Zero;
        IsPlaying = false;
    }

    public void Close()
    {
        Path = null;
        Duration = TimeSpan.Zero;
        Position = TimeSpan.Zero;
        IsPlaying = false;
    }

    public void Play()
    {
        if (Path is null)
        {
            return;
        }

        if (Position >= Duration)
        {
            Position = TimeSpan.Zero;
        }

        IsPlaying = true;
    }

    public void Pause() => IsPlaying = false;

    public void Seek(TimeSpan position) =>
        Position = position < TimeSpan.Zero ? TimeSpan.Zero
            : position > Duration ? Duration
            : position;

    /// <summary>Moves the clock on, as a real device's render thread would. Stops at the end.</summary>
    public void Advance(TimeSpan elapsed)
    {
        if (!IsPlaying)
        {
            return;
        }

        Position += elapsed;

        if (Position >= Duration)
        {
            Position = Duration;
            IsPlaying = false;
        }
    }

    public void Dispose() => Close();
}
