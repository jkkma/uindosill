using System.Runtime.InteropServices;

namespace Parakeet.Engine.ParakeetCpp.Interop;

/// <summary>
/// Owns a <c>parakeet_ctx*</c>.
/// </summary>
/// <remarks>
/// A <see cref="SafeHandle"/> rather than a raw pointer so the context cannot be collected
/// while a decode is running on it and cannot be leaked if the managed side throws. The
/// generated marshalling takes a reference for the duration of each call.
/// </remarks>
internal sealed class ParakeetContextHandle : SafeHandle
{
    public ParakeetContextHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        // parakeet_capi_free is documented safe on NULL, and never lets an exception cross the
        // boundary, so there is nothing here that can fail during finalisation.
        NativeMethods.parakeet_capi_free(handle);
        return true;
    }
}
