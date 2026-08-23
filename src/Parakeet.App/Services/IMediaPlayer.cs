namespace Parakeet.App.Services;

/// <summary>Thrown when a file cannot be played, carrying the reason a person can act on.</summary>
public sealed class PlaybackException : Exception
{
    public PlaybackException(string message)
        : base(message)
    {
    }

    public PlaybackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PlaybackException()
    {
    }
}

/// <summary>
/// Plays one file at a time — its sound, and its picture when it has one and the build can draw
/// one — with a position that can be read and moved.
/// </summary>
/// <remarks>
/// <para>
/// The application had no playback at all before the Ask tab: <c>Parakeet.Audio</c> decodes for
/// transcription and never sounds anything, and <c>scripts/preview-words-vtt.html</c> reads a
/// player's clock without ever setting it. So this is new surface rather than a wrapper over
/// something, and it is deliberately the smallest surface that a transcript-you-can-click needs —
/// open, play, pause, seek, a position to read, and a frame to copy. No volume, no rate, no
/// playlist.
/// </para>
/// <para>
/// There is no clock in here and nothing fires as the position moves. The caller reads
/// <see cref="Position"/> when it wants to draw, which keeps every thread question on the caller's
/// side of the interface and makes the whole thing testable by advancing a fake by hand. The one
/// thing that does fire is <see cref="FrameReady"/>, because a picture arrives at the decoder's
/// rate and not the caller's: a frame polled ten times a second is a slideshow. It says only that
/// there is something new to copy; the copy itself is pulled, on whatever thread the caller likes.
/// </para>
/// <para>
/// Two implementations, and the difference between them is a capability rather than a file.
/// <see cref="SystemAudioPlayer"/> plays sound through Media Foundation and WASAPI and draws
/// nothing, which is what every build has. <c>MpvMediaPlayer</c> plays sound and picture through
/// libmpv, which a build has only when that library has been vendored — see
/// <c>docs/NATIVE-BINARIES.md</c>. <see cref="CanDrawVideo"/> is how the window tells them apart,
/// and <see cref="MediaPlayers.ForThisBuild"/> picks.
/// </para>
/// </remarks>
public interface IMediaPlayer : IDisposable
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
    /// Whether this player can draw a picture at all. A property of the build rather than of any
    /// file: false means a video's sound plays and its picture does not, whatever is opened.
    /// </summary>
    bool CanDrawVideo { get; }

    /// <summary>
    /// Whether the open file has a picture this player is drawing. False when nothing is open, when
    /// the file is sound alone, and always when <see cref="CanDrawVideo"/> is false.
    /// </summary>
    bool HasVideo { get; }

    /// <summary>
    /// The size of the frames <see cref="TryCopyFrame"/> hands out, in pixels, or (0, 0) until the
    /// first one has been drawn.
    /// </summary>
    (int Width, int Height) FrameSize { get; }

    /// <summary>
    /// Raised when there is a new frame to copy. On an arbitrary thread — the decoder's, not the
    /// caller's — and it must return at once: copy later, on your own thread, through
    /// <see cref="TryCopyFrame"/>.
    /// </summary>
    event Action? FrameReady;

    /// <summary>
    /// Asks for frames of about this size, in pixels. The picture is scaled to fit inside it,
    /// keeping its shape, so the frames that follow may be smaller along one side. Cheap to call
    /// on every resize; nothing happens until the next frame.
    /// </summary>
    /// <remarks>
    /// The surface that will show the frame is the only thing that knows how large it is, and
    /// rendering at that size is both cheaper and sharper than rendering at the file's own — which
    /// for a 4K recording shown in a 600-pixel pane is sixteen times the pixels for nothing.
    /// </remarks>
    void SetVideoOutputSize(int width, int height);

    /// <summary>
    /// Copies the latest frame into <paramref name="destination"/> as rows of BGRA pixels — blue at
    /// the lowest address, alpha opaque — each row <paramref name="destinationRowBytes"/> apart.
    /// </summary>
    /// <returns>
    /// False when there is no frame, or when the destination is not <see cref="FrameSize"/>: a
    /// caller that sized its surface to a frame that has since changed shape re-sizes and tries
    /// again on the next <see cref="FrameReady"/>.
    /// </returns>
    bool TryCopyFrame(IntPtr destination, int destinationRowBytes, int destinationWidth, int destinationHeight);

    /// <summary>
    /// Opens <paramref name="path"/>, replacing whatever was open, stopped and at zero.
    /// </summary>
    /// <exception cref="PlaybackException">
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

/// <summary>Which player a build gets.</summary>
public static class MediaPlayers
{
    /// <summary>
    /// libmpv when it has been vendored beside the application, which plays picture and sound;
    /// otherwise the Media Foundation and WASAPI player, which plays sound alone.
    /// </summary>
    /// <remarks>
    /// Decided by what is on disk rather than by a setting, the way the transcription backends
    /// are: a build that carries the library uses it, and one that does not says so on the tab
    /// rather than offering a picture it cannot draw. Nothing here downloads anything.
    /// </remarks>
    public static IMediaPlayer ForThisBuild() =>
        Mpv.MpvNativeLibrary.IsPresent ? new Mpv.MpvMediaPlayer() : new SystemAudioPlayer();
}
