using NAudio.CoreAudioApi;
using NAudio.Wave;
using Parakeet.Audio;

namespace Parakeet.App.Services;

/// <summary>
/// Sound alone: Media Foundation or the managed WAVE reader for the file, WASAPI for the device.
/// </summary>
/// <remarks>
/// <para>
/// Both halves are already in this application's dependency graph — <c>NAudio.Wasapi</c> and
/// <c>NAudio.Core</c> arrive through <c>Parakeet.Audio</c>, which uses the first of them to decode
/// for transcription — so playback costs no new package and no second native stack. This is what
/// every build has; a build that has also vendored libmpv gets <c>MpvMediaPlayer</c> instead, which
/// draws the picture too.
/// </para>
/// <para>
/// Which reader opens a file follows <c>AudioSources.Open</c> rather than being decided again here:
/// WAVE goes to the managed reader on every platform because it handles RF64 and odd bit depths
/// predictably, and everything else needs Media Foundation and therefore Windows. Sniffed from the
/// magic bytes, not the extension, for the same reason that method does it. A video container opens
/// the same way and plays its sound track; <see cref="HasVideo"/> is false because the picture is
/// not decoded, not because it is not there.
/// </para>
/// <para>
/// The platform check is at run time rather than compiled away, which is the lesson
/// <c>Parakeet.Audio.csproj</c>'s own header records: a Windows path behind a compile-time switch
/// on a target framework nothing references is a path that ships unreachable in every build.
/// </para>
/// <para>
/// <b>Nothing in the suite exercises this class.</b> It needs a Windows audio endpoint, which CI
/// has not got and a headless run has not got either, so the tests drive
/// <see cref="FakeMediaPlayer"/> and this is covered by driving it against real files on a real
/// machine — see <c>docs/UNPROVEN.md</c> § <i>Playing a recording</i>, which also records the two
/// defects that run found in <see cref="Play"/>.
/// </para>
/// </remarks>
public sealed class SystemAudioPlayer : IMediaPlayer
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

    public bool CanDrawVideo => false;

    public bool HasVideo => false;

    public (int Width, int Height) FrameSize => (0, 0);

    /// <summary>Never raised: nothing here draws.</summary>
    public event Action? FrameReady
    {
        add { }
        remove { }
    }

    public void SetVideoOutputSize(int width, int height)
    {
    }

    public bool TryCopyFrame(IntPtr destination, int destinationRowBytes, int destinationWidth, int destinationHeight) => false;

    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Close();

        if (!File.Exists(path))
        {
            throw new PlaybackException($"'{System.IO.Path.GetFileName(path)}' is no longer where it was.");
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
            throw new PlaybackException($"Could not read '{name}': {ex.Message}", ex);
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
                // Not "use a WAVE file instead": playback itself goes through WASAPI, so on this
                // platform a WAVE would open and decode and then fail in CreateOutput — steering
                // somebody there hands them the opposite claim two clicks later.
                throw new PlaybackException(
                    $"'{name}' needs Media Foundation to decode, which exists only on Windows — " +
                    "and playback itself goes through WASAPI, so no recording plays on this " +
                    "platform. The transcript is readable here.");
            }

            return new MediaFoundationReader(path);
        }
        catch (Exception ex) when (ex is not PlaybackException and (IOException or FormatException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException))
        {
            throw new PlaybackException(
                $"Could not play '{name}' ({detection.Container}). This machine has no decoder for it. " +
                $"Underlying error: {ex.Message}",
                ex);
        }
    }

    private static WasapiOut CreateOutput(WaveStream reader)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlaybackException(
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

            throw new PlaybackException(
                "Windows would not open an audio output device. " +
                $"Underlying error: {ex.Message}",
                ex);
        }
    }
}
