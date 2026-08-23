using System.Runtime.InteropServices;

namespace Parakeet.App.Services.Mpv;

/// <summary>
/// The slice of libmpv's client and render APIs this application calls — nothing more.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written against <c>include/mpv/client.h</c> and <c>include/mpv/render.h</c> from the
/// vendored build (client API 2.5), the way <c>Parakeet.Engine.ParakeetCpp</c>'s interop is
/// hand-written against parakeet.h, and for the same reason: a binding package is a dependency
/// whose idea of the API has to be reconciled with the binary actually vendored, and this project
/// pins its binaries by digest. The constants below are the header's, copied by hand and checked
/// against it; they are ABI, not style, and must not be "tidied".
/// </para>
/// <para>
/// Everything takes and returns raw handles. The ownership rules live in
/// <see cref="MpvMediaPlayer"/>, which is the only caller; keeping this file dumb keeps the rules
/// in one place.
/// </para>
/// <para>
/// <c>mpv_client_api_version</c> returns C's <c>unsigned long</c>, which is 32-bit on Windows —
/// hence <see cref="uint"/> and not <see cref="ulong"/>. <c>mpv_render_context_update</c> returns
/// <c>uint64_t</c>, which is 64-bit everywhere.
/// </para>
/// </remarks>
internal static unsafe partial class MpvNative
{
    /// <summary>The import name the resolver in <see cref="MpvNativeLibrary"/> answers for.</summary>
    internal const string LibraryName = "libmpv-2";

    static MpvNative() => MpvNativeLibrary.RegisterResolver();

    // ── mpv_format ──────────────────────────────────────────────────────────────────────────
    internal const int FormatFlag = 3;
    internal const int FormatInt64 = 4;
    internal const int FormatDouble = 5;

    // ── mpv_event_id ────────────────────────────────────────────────────────────────────────
    internal const int EventShutdown = 1;
    internal const int EventEndFile = 7;
    internal const int EventFileLoaded = 8;

    // ── mpv_end_file_reason ─────────────────────────────────────────────────────────────────
    internal const int EndFileEof = 0;
    internal const int EndFileStop = 2;
    internal const int EndFileQuit = 3;
    internal const int EndFileError = 4;

    // ── mpv_render_param_type ───────────────────────────────────────────────────────────────
    internal const int RenderParamInvalid = 0;
    internal const int RenderParamApiType = 1;
    internal const int RenderParamSwSize = 17;
    internal const int RenderParamSwFormat = 18;
    internal const int RenderParamSwStride = 19;
    internal const int RenderParamSwPointer = 20;

    /// <summary>MPV_RENDER_UPDATE_FRAME: a new frame should be rendered.</summary>
    internal const ulong RenderUpdateFrame = 1;

    /// <summary>
    /// mpv_render_param: an int in a struct the size of two pointers. Sequential layout inserts
    /// the same 4 bytes of padding before the pointer on x64 that the C compiler does.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RenderParam
    {
        public int Type;
        public IntPtr Data;
    }

    [LibraryImport(LibraryName)]
    internal static partial uint mpv_client_api_version();

    [LibraryImport(LibraryName)]
    internal static partial IntPtr mpv_error_string(int error);

    [LibraryImport(LibraryName)]
    internal static partial IntPtr mpv_create();

    [LibraryImport(LibraryName)]
    internal static partial int mpv_initialize(IntPtr handle);

    [LibraryImport(LibraryName)]
    internal static partial void mpv_terminate_destroy(IntPtr handle);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_option_string(IntPtr handle, string name, string value);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_property_string(IntPtr handle, string name, string value);

    /// <summary>mpv_command over a NUL-terminated array of UTF-8 strings the caller has built.</summary>
    [LibraryImport(LibraryName)]
    internal static partial int mpv_command(IntPtr handle, IntPtr* args);

    [LibraryImport(LibraryName, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_get_property_double(IntPtr handle, string name, int format, out double value);

    [LibraryImport(LibraryName, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_get_property_int64(IntPtr handle, string name, int format, out long value);

    [LibraryImport(LibraryName, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_get_property_flag(IntPtr handle, string name, int format, out int value);

    /// <summary>Returns a pointer to an mpv_event, never null; valid until the next call.</summary>
    [LibraryImport(LibraryName)]
    internal static partial IntPtr mpv_wait_event(IntPtr handle, double timeoutSeconds);

    [LibraryImport(LibraryName)]
    internal static partial void mpv_wakeup(IntPtr handle);

    [LibraryImport(LibraryName)]
    internal static partial int mpv_render_context_create(out IntPtr context, IntPtr handle, RenderParam* parameters);

    [LibraryImport(LibraryName)]
    internal static partial void mpv_render_context_set_update_callback(
        IntPtr context, delegate* unmanaged[Cdecl]<IntPtr, void> callback, IntPtr callbackContext);

    [LibraryImport(LibraryName)]
    internal static partial ulong mpv_render_context_update(IntPtr context);

    [LibraryImport(LibraryName)]
    internal static partial int mpv_render_context_render(IntPtr context, RenderParam* parameters);

    [LibraryImport(LibraryName)]
    internal static partial void mpv_render_context_free(IntPtr context);

    /// <summary>The library's text for an error code. Static storage on its side; nothing to free.</summary>
    internal static string ErrorText(int error) =>
        Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? $"mpv error {error}";
}
