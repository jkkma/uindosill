using System.Runtime.InteropServices;

namespace Parakeet.Engine.ParakeetCpp.Interop;

/// <summary>
/// Sets an environment variable so that <c>getenv</c> inside a loaded native library can see it.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Environment.SetEnvironmentVariable(string, string?)"/> does not do this on
/// Windows</b>, and the failure is silent. It calls <c>SetEnvironmentVariableW</c>, which updates
/// the process environment block, while the UCRT keeps a separate table that it populates once at
/// startup and thereafter only through <c>_putenv</c>. ggml reads its knobs with <c>getenv</c>, so
/// a value set the managed way is invisible to it and the call appears to do nothing.
/// </para>
/// <para>
/// Measured rather than assumed. Setting <c>GGML_VK_DISABLE_BFLOAT16=1</c> through
/// <c>Environment.SetEnvironmentVariable</c> before loading a model left the load failing exactly
/// as it does with no variable set; the same value through <c>ucrtbase!_putenv</c> in the same
/// position made it load. See UNPROVEN.md in the project notes.
/// </para>
/// <para>
/// Both are written: the CRT copy is what native code reads, and the managed copy keeps
/// <see cref="Environment.GetEnvironmentVariable(string)"/> agreeing with it, so a later reader in
/// managed code is not told something different from what the library was given.
/// </para>
/// <para>
/// This must run <em>before</em> the variable is read. ggml reads its Vulkan knobs during device
/// initialisation, which happens inside the model load, and that initialisation happens once per
/// process — setting a knob after a failed load and trying again does not re-read it, it crashes.
/// </para>
/// </remarks>
internal static partial class NativeEnvironment
{
    /// <summary>
    /// Sets <paramref name="name"/> for both managed code and any native library in this process.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the native table was updated. <see langword="false"/> means only
    /// the managed copy was set, so native code will not see it — callers that need the value to
    /// reach a native library should treat that as the operation having failed.
    /// </returns>
    public static bool Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        Environment.SetEnvironmentVariable(name, value);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return CrtPutEnv($"{name}={value}") == 0;
            }

            return SetEnv(name, value, overwrite: 1) == 0;
        }
        // A runtime without the expected C runtime export leaves the managed copy set and reports
        // failure, rather than taking the process down inside a diagnostic convenience.
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>True when the variable already has a value, from any source.</summary>
    public static bool IsSet(string name) =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name));

    [LibraryImport("ucrtbase.dll", EntryPoint = "_putenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CrtPutEnv(string assignment);

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnv(string name, string value, int overwrite);
}
