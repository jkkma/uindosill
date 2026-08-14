using System.Runtime.InteropServices;

namespace Parakeet.Engine.ParakeetCpp.Interop;

/// <summary>
/// The parakeet.cpp flat C ABI, version 6.
/// </summary>
/// <remarks>
/// <para>
/// Source-generated <see cref="LibraryImportAttribute"/> rather than <c>DllImport</c>: the
/// marshalling is visible, compile-checked and free of the runtime IL stub.
/// </para>
/// <para>
/// Every function that returns <c>char*</c> is declared as <see cref="IntPtr"/> on purpose.
/// The strings are <c>malloc</c>'d by the native side and must be released with
/// <c>parakeet_capi_free_string</c>; the default string marshaller would free them with
/// <c>CoTaskMemFree</c>, which is a different allocator and corrupts the heap. Use
/// <see cref="NativeString.Consume"/>, which frees in a <c>finally</c>.
/// </para>
/// <para>
/// The one exception is <c>parakeet_capi_last_error</c>: that pointer is owned by the context
/// and valid only until the next call on it, so it is read and never freed.
/// </para>
/// </remarks>
internal static unsafe partial class NativeMethods
{
    /// <summary>
    /// Base name passed to the loader. The real file is located by
    /// <see cref="ParakeetNativeLibrary"/>, which knows about backend subdirectories.
    /// </summary>
    internal const string LibraryName = "parakeet";

    /// <summary>ABI this binding is written against. A mismatch is refused loudly at load.</summary>
    internal const int ExpectedAbiVersion = 6;

    [LibraryImport(LibraryName)]
    internal static partial int parakeet_capi_abi_version();

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ParakeetContextHandle parakeet_capi_load(string ggufPath);

    /// <summary>Safe on NULL. Called only from <see cref="ParakeetContextHandle.ReleaseHandle"/>.</summary>
    [LibraryImport(LibraryName)]
    internal static partial void parakeet_capi_free(IntPtr ctx);

    /// <summary>
    /// Single-clip decode returning bare text. Kept for the load-time smoke check, where the
    /// question is only whether the model produces anything at all.
    /// </summary>
    [LibraryImport(LibraryName)]
    internal static partial IntPtr parakeet_capi_transcribe_pcm(
        ParakeetContextHandle ctx, float* samples, int nSamples, int sampleRate, int decoder);

    /// <summary>
    /// Batched decode with timestamps. Returns ONE JSON string holding an array of per-clip
    /// documents.
    /// </summary>
    /// <remarks>
    /// Precondition the caller must uphold and the native side does not validate: the sum of
    /// <paramref name="nSamples"/> equals the number of floats in
    /// <paramref name="samplesConcat"/>. A larger sum reads out of bounds.
    /// </remarks>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr parakeet_capi_transcribe_pcm_batch_json_lang(
        ParakeetContextHandle ctx,
        float* samplesConcat,
        int* nSamples,
        int nClips,
        int sampleRate,
        int decoder,
        string? targetLang);

    /// <summary>
    /// Offline TDT beam search. Diagnostics only — beam search on Parakeet TDT is a measured
    /// regression against greedy, not an upgrade.
    /// </summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr parakeet_capi_transcribe_pcm_nbest_json(
        ParakeetContextHandle ctx,
        float* samples,
        int nSamples,
        int sampleRate,
        int beamSize,
        int nbest,
        int scoreNorm,
        string? targetLang);

    /// <summary>Owned by the context. Read it; never free it.</summary>
    [LibraryImport(LibraryName)]
    internal static partial IntPtr parakeet_capi_last_error(ParakeetContextHandle ctx);

    [LibraryImport(LibraryName)]
    internal static partial void parakeet_capi_free_string(IntPtr value);
}
