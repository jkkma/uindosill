using System.Runtime.InteropServices;

namespace Parakeet.Engine.ParakeetCpp.Interop;

/// <summary>Marshals and releases the malloc'd UTF-8 strings the C ABI returns.</summary>
internal static class NativeString
{
    /// <summary>
    /// Copies a returned <c>char*</c> into a managed string and frees the native buffer, even
    /// if the copy throws.
    /// </summary>
    /// <returns>Null when the pointer is null, which the ABI uses to signal failure.</returns>
    internal static string? Consume(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            NativeMethods.parakeet_capi_free_string(pointer);
        }
    }

    /// <summary>
    /// Reads a borrowed <c>const char*</c> without freeing it. Used for the context's last
    /// error, whose storage belongs to the context.
    /// </summary>
    internal static string Borrow(IntPtr pointer) =>
        pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
}
