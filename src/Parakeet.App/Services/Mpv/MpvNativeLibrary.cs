using System.Runtime.InteropServices;

namespace Parakeet.App.Services.Mpv;

/// <summary>
/// Finds and loads the vendored libmpv, and answers whether this build has one at all.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <c>ParakeetNativeLibrary</c>, scaled down: libmpv has no backends and no
/// sibling DLLs — the vendored build is one statically linked file — so the search is four
/// directories and two file names rather than a matrix. The layout it expects is the one
/// <c>scripts/vendor-mpv.ps1</c> produces and <c>docs/NATIVE-BINARIES.md</c> describes:
/// <c>native/win-x64/mpv/libmpv-2.dll</c> beside the application, with its notice beside it.
/// </para>
/// <para>
/// <see cref="IsPresent"/> is what makes video a property of the build rather than a promise:
/// <c>MediaPlayers.ForThisBuild</c> asks it once, and a build without the library gets the
/// audio-only player and a tab that says so, instead of an exception out of a DllImport.
/// </para>
/// </remarks>
internal static class MpvNativeLibrary
{
    /// <summary>Overrides the search, for a developer with the library somewhere else.</summary>
    internal const string DirectoryEnvironmentVariable = "UINDOSILL_MPV_NATIVE_DIR";

    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>The file the resolver loaded, for diagnostics; null until the first load.</summary>
    internal static string? LoadedPath { get; private set; }

    /// <summary>Whether a libmpv is on disk where this build would load it from.</summary>
    internal static bool IsPresent => OperatingSystem.IsWindows() && Locate() is not null;

    /// <summary>
    /// The library file this build would load, or null. Probed fresh on each call — it is asked
    /// once per player construction, not per frame.
    /// </summary>
    internal static string? Locate()
    {
        foreach (var directory in CandidateDirectories())
        {
            // libmpv-2.dll is the name since mpv 0.35; mpv-2.dll the one before it. Both are the
            // same ABI major, so accepting either costs nothing and saves a rename question.
            foreach (var name in new[] { "libmpv-2.dll", "mpv-2.dll" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Registers the resolver that maps <see cref="MpvNative.LibraryName"/> to the located file.
    /// Called from <see cref="MpvNative"/>'s static constructor, so it has run before any import.
    /// </summary>
    /// <remarks>
    /// One resolver per assembly is a runtime rule, and this is the only one registered for
    /// Parakeet.App — parakeet.cpp's is on its own engine assembly. If a second native ever
    /// arrives in this assembly, this resolver grows a case rather than a sibling.
    /// </remarks>
    internal static void RegisterResolver()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(typeof(MpvNativeLibrary).Assembly, Resolve);
            _registered = true;
        }
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != MpvNative.LibraryName)
        {
            return IntPtr.Zero;
        }

        if (LoadedPath is not null && NativeLibrary.TryLoad(LoadedPath, out var already))
        {
            return already;
        }

        if (Locate() is { } located && NativeLibrary.TryLoad(located, out var handle))
        {
            LoadedPath = located;
            return handle;
        }

        // Zero hands the decision back to the default loader, whose failure message names the
        // library; MediaPlayers.ForThisBuild means a working install never gets here.
        return IntPtr.Zero;
    }

    /// <summary>
    /// Rooted, like the parakeet loader's, and for the recorded reason: a relative path passes
    /// File.Exists against the working directory and then loads without the module-directory
    /// search. libmpv has no siblings today, so this is cheap insurance rather than a live bug.
    /// </summary>
    private static IEnumerable<string> CandidateDirectories()
    {
        if (Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            yield return Path.GetFullPath(fromEnvironment);
        }

        var baseDirectory = AppContext.BaseDirectory;

        yield return Path.Combine(baseDirectory, "native", "win-x64", "mpv");
        yield return Path.Combine(baseDirectory, "native", "mpv");
        yield return baseDirectory;
    }
}
