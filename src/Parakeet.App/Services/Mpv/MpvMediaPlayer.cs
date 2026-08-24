using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Parakeet.App.Services.Mpv.MpvNative;

namespace Parakeet.App.Services.Mpv;

/// <summary>
/// Sound and picture through libmpv: the file is decoded, synchronised and sounded by mpv's own
/// player core, and each video frame is software-rendered into a buffer this class hands to the
/// window as BGRA.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a whole player rather than a decoder.</b> The hard part of video is not decoding, it is
/// being a player: keeping the picture on the sound's clock, seeking both together, and knowing
/// where the end is. Media Foundation gives the decode and leaves the player to be written;
/// libmpv is the player, finished, behind a C API of a dozen calls. That is the trade
/// <c>docs/PHASES.md</c> § <i>Decided 2026-08-23</i> records, along with what it costs — the
/// binary, and the licence.
/// </para>
/// <para>
/// <b>The render path is the software one, by decision.</b> libmpv's own header calls it "very
/// slow ... single-threaded" — against its OpenGL path, for full-rate high-resolution video. Here
/// the output is a pane a few hundred pixels wide, the render size follows the pane rather than
/// the file (<see cref="SetVideoOutputSize"/>), and the alternative is GL context interop with
/// Avalonia's compositor, which is exactly the kind of surface this application has no test for.
/// Measured before shipping rather than assumed: see <c>docs/UNPROVEN.md</c> § <i>Playing a
/// recording</i>.
/// </para>
/// <para>
/// <b>Threads.</b> mpv's client API is thread-safe, so property reads answer on whatever thread
/// asks — which is what lets <see cref="Position"/> stay a plain getter the window polls, the same
/// contract the audio player has. Two threads are this class's own: an event thread blocked in
/// <c>mpv_wait_event</c>, which is how load results and end-of-file arrive, and a render thread
/// that waits for mpv's update callback, renders the frame, and raises <see cref="FrameReady"/>.
/// The callback itself does nothing but set an event, as the header requires.
/// </para>
/// <para>
/// <b>Nothing in the suite runs this class</b>, for the same reason nothing runs
/// <see cref="SystemAudioPlayer"/>: it needs the vendored library and an audio endpoint, and CI
/// has neither. The tests drive <see cref="FakeMediaPlayer"/>; this is driven against real files
/// on a real machine, and <c>docs/UNPROVEN.md</c> records what that has and has not established.
/// </para>
/// </remarks>
public sealed unsafe class MpvMediaPlayer : IMediaPlayer
{
    /// <summary>How long a load may take before Open stops waiting and calls it a failure.</summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The same, for a link, which has a network round trip and a yt-dlp process in it.</summary>
    private static readonly TimeSpan OpenUrlTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The box frames are rendered into until the window says how big its pane is.</summary>
    private static readonly (int Width, int Height) DefaultOutputBox = (1280, 720);

    private readonly IntPtr _handle;
    private readonly IntPtr _renderContext;
    private readonly Thread _eventThread;
    private readonly Thread _renderThread;
    private readonly AutoResetEvent _renderWake = new(false);
    private readonly ManualResetEventSlim _loaded = new(false);
    private readonly GCHandle _self;

    /// <summary>Guards the front buffer and everything describing it.</summary>
    private readonly object _frameLock = new();

    // Double buffer: the render thread owns the back buffer outside the lock, the window copies
    // from the front buffer inside it, and a finished frame is a pointer swap rather than a copy.
    private FrameBuffer _front;
    private FrameBuffer _back;
    private bool _hasFrame;

    private volatile string? _loadError;
    private volatile bool _fileLoaded;
    private volatile bool _stopping;
    private (int Width, int Height) _requestedBox;
    private bool _disposed;

    private struct FrameBuffer
    {
        public IntPtr Pixels;
        public int Width;
        public int Height;
        public int Stride;
        public nuint Capacity;
    }

    public MpvMediaPlayer()
    {
        // The SW render API arrived with client API 2.0 (mpv 0.33). A vendored build older than
        // that would fail at render-context creation with a worse message than this one.
        var version = mpv_client_api_version();
        if (version < 2u << 16)
        {
            throw new PlaybackException(
                $"The vendored libmpv speaks client API {version >> 16}.{version & 0xFFFF}; this build needs 2.0 or later.");
        }

        _handle = mpv_create();
        if (_handle == IntPtr.Zero)
        {
            throw new PlaybackException("libmpv refused to create a player core.");
        }

        try
        {
            // Every option here is the application deciding, not a default left alone. config=no
            // and load-scripts=no keep a user's own mpv.conf and scripts out of this process —
            // this is a pane in a transcription window, not their mpv. vo=libmpv routes video into
            // the render context instead of a window of mpv's own. keep-open holds the file at its
            // end instead of unloading it, which is what lets play-at-the-end mean "start over".
            // idle keeps the core alive between files. audio-display=no stops an mp3's embedded
            // cover art from becoming a video track — HasVideo means moving pictures here. And
            // pause=yes is the contract: Open leaves the recording stopped at zero.
            SetOption("config", "no");
            SetOption("load-scripts", "no");
            SetOption("osc", "no");
            SetOption("input-default-bindings", "no");
            SetOption("terminal", "no");
            SetOption("ytdl", "no");
            SetOption("vo", "libmpv");
            SetOption("keep-open", "yes");
            SetOption("idle", "yes");
            SetOption("audio-display", "no");
            SetOption("pause", "yes");

            // Streaming a link. mpv's ytdl_hook is built into the binary and gated on this option
            // rather than on load-scripts, so the user's own scripts stay out (config=no,
            // load-scripts=no above) while this one is admitted. It is pointed at the vendored
            // yt-dlp by absolute path rather than left to find one on PATH, so a different copy
            // installed on the machine cannot silently take over from the pinned one.
            //
            // Deno *is* left to PATH, because that is where yt-dlp looks for it and mpv spawns
            // yt-dlp itself — see BundledTools.PrependToPath, which is what puts it there.
            if (Tools.BundledTools.YtDlpPath is { } ytDlp)
            {
                Tools.BundledTools.PrependToPath();
                SetOption("ytdl", "yes");

                // The path is escaped because script-opts is a comma-separated key=value list and
                // a Windows path contains neither a comma nor an equals sign — but may contain a
                // percent, which is the escape character mpv uses for values needing one. The
                // %n%string form gives a length-prefixed literal, which needs no escaping at all.
                SetOption("script-opts", $"ytdl_hook-ytdl_path=%{ytDlp.Length}%{ytDlp}");
            }
            else
            {
                SetOption("ytdl", "no");
            }

            var initialised = mpv_initialize(_handle);
            if (initialised < 0)
            {
                throw new PlaybackException($"libmpv failed to initialise: {ErrorText(initialised)}.");
            }

            // The render context, before any file: with vo=libmpv a video track has nowhere to go
            // until one exists, and creating it once here is what makes that "until" empty.
            var apiType = "sw\0"u8;
            fixed (byte* api = apiType)
            {
                var parameters = stackalloc RenderParam[2];
                parameters[0] = new RenderParam { Type = RenderParamApiType, Data = (IntPtr)api };
                parameters[1] = new RenderParam { Type = RenderParamInvalid, Data = IntPtr.Zero };

                var created = mpv_render_context_create(out _renderContext, _handle, parameters);
                if (created < 0)
                {
                    throw new PlaybackException($"libmpv refused a software render context: {ErrorText(created)}.");
                }
            }

            _self = GCHandle.Alloc(this);
            mpv_render_context_set_update_callback(_renderContext, &OnRenderUpdate, GCHandle.ToIntPtr(_self));

            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
            _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "mpv render" };
            _eventThread.Start();
            _renderThread.Start();
        }
        catch
        {
            if (_renderContext != IntPtr.Zero)
            {
                mpv_render_context_free(_renderContext);
            }

            mpv_terminate_destroy(_handle);

            if (_self.IsAllocated)
            {
                _self.Free();
            }

            throw;
        }
    }

    public string? Path { get; private set; }

    public TimeSpan Duration =>
        mpv_get_property_double(_handle, "duration", FormatDouble, out var seconds) >= 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;

    public TimeSpan Position =>
        mpv_get_property_double(_handle, "time-pos", FormatDouble, out var seconds) >= 0
            ? TimeSpan.FromSeconds(Math.Max(0, seconds))
            : TimeSpan.Zero;

    public bool IsPlaying =>
        _fileLoaded
        && mpv_get_property_flag(_handle, "pause", FormatFlag, out var paused) >= 0
        && paused == 0;

    public bool CanDrawVideo => true;

    /// <summary>
    /// Whether mpv is decoding a video track right now. dwidth exists only while one is
    /// configured, so the read doubles as the check; cover art is already excluded by
    /// audio-display=no.
    /// </summary>
    public bool HasVideo =>
        _fileLoaded
        && mpv_get_property_int64(_handle, "dwidth", FormatInt64, out var width) >= 0
        && width > 0;

    public (int Width, int Height) FrameSize
    {
        get
        {
            lock (_frameLock)
            {
                return _hasFrame ? (_front.Width, _front.Height) : (0, 0);
            }
        }
    }

    public event Action? FrameReady;

    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A link is handed straight to mpv, which resolves it through yt-dlp and streams it. That
        // is the whole of video-from-a-link: the file on disk beside it is the audio the transcript
        // was made from, and the picture never needs downloading.
        var isUrl = Uri.TryCreate(path, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        if (!isUrl && !File.Exists(path))
        {
            throw new PlaybackException($"'{System.IO.Path.GetFileName(path)}' is no longer where it was.");
        }

        if (isUrl && !Tools.BundledTools.CanFetchUrls)
        {
            throw new PlaybackException(
                Tools.BundledTools.DescribeUnavailable() ?? "Uindosill cannot open links.");
        }

        Close();

        // Resolving a link is a network round trip through a Python process, which is a different
        // order of wait from opening a file. Twenty seconds is generous for a file and tight for a
        // slow connection, so a link gets its own bound.
        var timeout = isUrl ? OpenUrlTimeout : OpenTimeout;

        _loaded.Reset();
        _loadError = null;

        SetProperty("pause", "yes");
        Command("loadfile", path, "replace");

        // Synchronous on purpose: the interface promises that a file Open returned for is open,
        // with a duration, and every failure is an exception rather than a state to poll. The
        // wait is on the event thread's FILE_LOADED or END_FILE, and twenty seconds is not a
        // number any local file should meet.
        if (!_loaded.Wait(timeout))
        {
            Command("stop");
            throw new PlaybackException(
                $"'{Describe(path)}' did not finish opening within {timeout.TotalSeconds:0} seconds.");
        }

        if (_loadError is { } reason)
        {
            throw new PlaybackException($"Could not play '{Describe(path)}'. {reason}.");
        }

        Path = path;
    }

    public void Close()
    {
        if (_disposed || Path is null && !_fileLoaded)
        {
            return;
        }

        Command("stop");
        _fileLoaded = false;
        Path = null;

        lock (_frameLock)
        {
            _hasFrame = false;
        }
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Path is null)
        {
            return;
        }

        // The same wrap the audio player carries, answered by the core rather than arithmetic:
        // with keep-open=yes mpv pauses at the end and raises eof-reached, and play from there
        // means from the top.
        if (mpv_get_property_flag(_handle, "eof-reached", FormatFlag, out var ended) >= 0 && ended != 0)
        {
            Command("seek", "0", "absolute");
        }

        SetProperty("pause", "no");
    }

    public void Pause()
    {
        if (!_disposed && Path is not null)
        {
            SetProperty("pause", "yes");
        }
    }

    public void Seek(TimeSpan position)
    {
        if (_disposed || Path is null)
        {
            return;
        }

        var duration = Duration;
        var target = position < TimeSpan.Zero ? TimeSpan.Zero
            : duration > TimeSpan.Zero && position > duration ? duration
            : position;

        // absolute+exact, not absolute: mpv's default seek lands on a keyframe, which on a
        // long-GOP file is whole seconds away from the cue that was clicked — and a citation that
        // plays the wrong sentence is this feature failing at the one thing it is for. Exact
        // seeking decodes forward from the keyframe instead, which costs milliseconds.
        Command("seek", target.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact");
    }

    public void SetVideoOutputSize(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            // Read by the render thread at its next frame; no lock needed beyond the tuple being
            // written atomically enough for a size, and a torn read here would only mis-size one
            // frame. Kept simple on purpose.
            _requestedBox = (width, height);
        }
    }

    public bool TryCopyFrame(IntPtr destination, int destinationRowBytes, int destinationWidth, int destinationHeight)
    {
        lock (_frameLock)
        {
            if (!_hasFrame || destinationWidth != _front.Width || destinationHeight != _front.Height)
            {
                return false;
            }

            for (var y = 0; y < _front.Height; y++)
            {
                var source = new Span<uint>((byte*)_front.Pixels + (long)y * _front.Stride, _front.Width);
                var target = new Span<uint>((byte*)destination + (long)y * destinationRowBytes, _front.Width);

                // The copy is also the alpha fill: mpv's "bgr0" leaves the fourth byte as
                // uninitialised garbage, and Avalonia is handed Bgra8888 — a frame whose alpha
                // bytes happened to be zero would composite as nothing at all.
                for (var x = 0; x < source.Length; x++)
                {
                    target[x] = source[x] | 0xFF000000u;
                }
            }

            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // quit, then join the event thread: the event loop exits on the SHUTDOWN event, which is
        // how the header wants a multi-threaded teardown sequenced — no thread may still be in
        // mpv_wait_event when the handle is destroyed.
        Command("quit");

        if (!_eventThread.Join(TimeSpan.FromSeconds(5)))
        {
            mpv_wakeup(_handle);
            _eventThread.Join(TimeSpan.FromSeconds(2));
        }

        _stopping = true;
        _renderWake.Set();
        _renderThread.Join(TimeSpan.FromSeconds(5));

        // Render context before handle — the header's ordering — and the callback cleared first,
        // so a straggling core thread cannot call into a freed context's waker.
        mpv_render_context_set_update_callback(_renderContext, null, IntPtr.Zero);
        mpv_render_context_free(_renderContext);
        mpv_terminate_destroy(_handle);

        lock (_frameLock)
        {
            FreeBuffer(ref _front);
            FreeBuffer(ref _back);
            _hasFrame = false;
        }

        _self.Free();
        _renderWake.Dispose();
        _loaded.Dispose();
    }

    /// <summary>mpv's update callback: set the render thread's alarm and get out, as the header
    /// demands — no mpv call is legal from inside it.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRenderUpdate(IntPtr opaque)
    {
        if (GCHandle.FromIntPtr(opaque).Target is MpvMediaPlayer player)
        {
            player._renderWake.Set();
        }
    }

    private void EventLoop()
    {
        while (true)
        {
            var eventPointer = mpv_wait_event(_handle, timeoutSeconds: -1);
            var eventId = Marshal.ReadInt32(eventPointer);

            switch (eventId)
            {
                case EventShutdown:
                    return;

                case EventFileLoaded:
                    _fileLoaded = true;
                    _loaded.Set();
                    break;

                case EventEndFile:
                    // The event's payload is valid only until the next wait, so what matters is
                    // copied out here: why it ended, and mpv's own words when that is an error.
                    var data = Marshal.ReadIntPtr(eventPointer, 16);
                    var reason = Marshal.ReadInt32(data);

                    if (reason == EndFileError)
                    {
                        _loadError = ErrorText(Marshal.ReadInt32(data, 4));
                        _fileLoaded = false;
                        _loaded.Set();
                    }
                    else if (reason is EndFileStop or EndFileQuit)
                    {
                        _fileLoaded = false;
                    }

                    break;
            }
        }
    }

    private void RenderLoop()
    {
        while (true)
        {
            _renderWake.WaitOne();

            if (_stopping)
            {
                return;
            }

            if ((mpv_render_context_update(_renderContext) & RenderUpdateFrame) != 0)
            {
                RenderFrame();
            }
        }
    }

    private void RenderFrame()
    {
        // The display size, not the storage size: dwidth/dheight carry the aspect correction for
        // anamorphic files, and they exist only while a video track is configured — their absence
        // is a frame nobody needs.
        if (mpv_get_property_int64(_handle, "dwidth", FormatInt64, out var videoWidth) < 0
            || mpv_get_property_int64(_handle, "dheight", FormatInt64, out var videoHeight) < 0
            || videoWidth <= 0 || videoHeight <= 0)
        {
            return;
        }

        // Fit into the pane's box without ever upscaling: pixels the pane does not have are pure
        // cost here, and pixels beyond the file's own are cost for nothing — Avalonia's Uniform
        // stretch does any remaining enlargement on the GPU.
        var box = _requestedBox is { Width: > 0, Height: > 0 } requested ? requested : DefaultOutputBox;
        var scale = Math.Min(1.0, Math.Min(box.Width / (double)videoWidth, box.Height / (double)videoHeight));
        var width = Math.Max(2, (int)Math.Round(videoWidth * scale));
        var height = Math.Max(2, (int)Math.Round(videoHeight * scale));

        // Stride a multiple of 64, and the allocation 64-aligned, which render.h names as the
        // alignment that keeps mpv's copy on its fast path.
        var stride = (width * 4 + 63) & ~63;
        var needed = (nuint)stride * (nuint)height;

        if (_back.Capacity < needed)
        {
            FreeBuffer(ref _back);
            _back.Pixels = (IntPtr)NativeMemory.AlignedAlloc(needed, 64);
            _back.Capacity = needed;
        }

        _back.Width = width;
        _back.Height = height;
        _back.Stride = stride;

        var size = stackalloc int[2] { width, height };
        var strideValue = (nuint)stride;
        var format = "bgr0\0"u8;

        int rendered;
        fixed (byte* formatPointer = format)
        {
            var parameters = stackalloc RenderParam[5];
            parameters[0] = new RenderParam { Type = RenderParamSwSize, Data = (IntPtr)size };
            parameters[1] = new RenderParam { Type = RenderParamSwFormat, Data = (IntPtr)formatPointer };
            parameters[2] = new RenderParam { Type = RenderParamSwStride, Data = (IntPtr)(&strideValue) };
            parameters[3] = new RenderParam { Type = RenderParamSwPointer, Data = _back.Pixels };
            parameters[4] = new RenderParam { Type = RenderParamInvalid, Data = IntPtr.Zero };

            rendered = mpv_render_context_render(_renderContext, parameters);
        }

        if (rendered < 0)
        {
            return;
        }

        lock (_frameLock)
        {
            (_front, _back) = (_back, _front);
            _hasFrame = true;
        }

        FrameReady?.Invoke();
    }

    private void SetOption(string name, string value)
    {
        var result = mpv_set_option_string(_handle, name, value);
        if (result < 0)
        {
            throw new PlaybackException($"libmpv rejected {name}={value}: {ErrorText(result)}.");
        }
    }

    private void SetProperty(string name, string value)
    {
        // Best-effort by design: the properties set here (pause) cannot fail on a loaded file,
        // and on an unloaded one failing quietly is the contract the audio player set.
        _ = mpv_set_property_string(_handle, name, value);
    }

    private void Command(params string[] arguments)
    {
        var pointers = new IntPtr[arguments.Length + 1];

        try
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                pointers[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
            }

            fixed (IntPtr* args = pointers)
            {
                _ = mpv_command(_handle, args);
            }
        }
        finally
        {
            foreach (var pointer in pointers)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
    }

    /// <summary>A file by its name and a link by its host, for a message somebody has to read.</summary>
    private static string Describe(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.Ordinal)
            ? uri.Host
            : System.IO.Path.GetFileName(path);

    private static void FreeBuffer(ref FrameBuffer buffer)
    {
        if (buffer.Pixels != IntPtr.Zero)
        {
            NativeMemory.AlignedFree((void*)buffer.Pixels);
            buffer = default;
        }
    }
}
