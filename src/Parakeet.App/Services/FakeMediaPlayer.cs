namespace Parakeet.App.Services;

/// <summary>
/// A player with a clock and no sound, whose position moves only when it is told to, and whose
/// picture is a flat colour it paints on request.
/// </summary>
/// <remarks>
/// What the tests drive, for the reason <see cref="SystemAudioPlayer"/> gives: WASAPI needs an
/// output device, libmpv needs its library vendored, and neither CI nor a headless run has either.
/// Everything the window does with a player — open, play, seek from a transcript cue, follow the
/// position, stop at the end, size a surface to a frame and copy one in — is exercised through
/// this, so what is untested is the device rather than the behaviour.
/// </remarks>
public sealed class FakeMediaPlayer : IMediaPlayer
{
    /// <summary>How long anything it opens turns out to be.</summary>
    public TimeSpan DurationToReport { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// When set, <see cref="Open"/> refuses with this message instead of opening — which is how a
    /// test drives the window's unplayable-file path without needing an unplayable file.
    /// </summary>
    public string? RefuseWith { get; set; }

    /// <summary>
    /// The picture the next <see cref="Open"/> finds, or null for sound alone. A test sets this to
    /// stand in for a video file, since no real one is opened.
    /// </summary>
    public (int Width, int Height)? VideoToReport { get; set; }

    /// <summary>What <see cref="TryCopyFrame"/> paints, as BGRA.</summary>
    public uint FrameColour { get; set; } = 0xFF3366CC;

    /// <summary>The last size the surface asked for, so a test can see that it was passed on.</summary>
    public (int Width, int Height)? RequestedOutputSize { get; private set; }

    public string? Path { get; private set; }

    public TimeSpan Duration { get; private set; }

    public TimeSpan Position { get; private set; }

    public bool IsPlaying { get; private set; }

    /// <summary>True, because the fake stands in for the fullest player; a test that wants the
    /// audio-only build sets this false.</summary>
    public bool CanDrawVideo { get; set; } = true;

    public bool HasVideo => CanDrawVideo && Path is not null && VideoToReport is not null;

    public (int Width, int Height) FrameSize { get; private set; }

    public event Action? FrameReady;

    /// <summary>How many times a file has been opened, so a test can see a re-open it did not want.</summary>
    public int Opens { get; private set; }

    /// <summary>How many frames were copied out, so a test can see that a frame arrived on a surface.</summary>
    public int FramesCopied { get; private set; }

    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (RefuseWith is { Length: > 0 } reason)
        {
            Close();
            throw new PlaybackException(reason);
        }

        Opens++;
        Path = path;
        Duration = DurationToReport;
        Position = TimeSpan.Zero;
        IsPlaying = false;
        FrameSize = HasVideo ? VideoToReport!.Value : (0, 0);
    }

    public void Close()
    {
        Path = null;
        Duration = TimeSpan.Zero;
        Position = TimeSpan.Zero;
        IsPlaying = false;
        FrameSize = (0, 0);
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

    public void SetVideoOutputSize(int width, int height) => RequestedOutputSize = (width, height);

    public unsafe bool TryCopyFrame(IntPtr destination, int destinationRowBytes, int destinationWidth, int destinationHeight)
    {
        if (!HasVideo || (destinationWidth, destinationHeight) != FrameSize)
        {
            return false;
        }

        for (var y = 0; y < destinationHeight; y++)
        {
            var row = new Span<uint>((byte*)destination + (long)y * destinationRowBytes, destinationWidth);
            row.Fill(FrameColour);
        }

        FramesCopied++;
        return true;
    }

    /// <summary>Announces a frame, as the decoder would. The surface then comes and copies it.</summary>
    public void RaiseFrame() => FrameReady?.Invoke();

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
