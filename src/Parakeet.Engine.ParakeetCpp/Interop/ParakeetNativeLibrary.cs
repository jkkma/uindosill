using System.Runtime.InteropServices;
using System.Text;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.ParakeetCpp.Interop;

public sealed class ParakeetNativeLoadException : Exception
{
    public ParakeetNativeLoadException(string message)
        : base(message)
    {
    }

    public ParakeetNativeLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ParakeetNativeLoadException()
    {
    }
}

public sealed class ParakeetAbiMismatchException : Exception
{
    public ParakeetAbiMismatchException(int expected, int actual)
        : base($"parakeet.cpp reports ABI version {actual}; this build is written against version {expected}. " +
               "Refusing to continue: the signatures and ownership rules differ between ABI versions, and " +
               "guessing corrupts memory rather than failing cleanly.")
    {
        Expected = expected;
        Actual = actual;
    }

    public ParakeetAbiMismatchException()
    {
    }

    public ParakeetAbiMismatchException(string message)
        : base(message)
    {
    }

    public ParakeetAbiMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public int Expected { get; }

    public int Actual { get; }
}

/// <summary>
/// Finds and loads the parakeet.cpp shared library, choosing a compute backend.
/// </summary>
/// <remarks>
/// <para>
/// Backends live in sibling directories (<c>native/win-x64/vulkan</c>, <c>.../cuda</c>,
/// <c>.../cpu</c>) rather than being selected by an environment variable inside one directory,
/// so which binary is loaded is a property of the file system and can be inspected after the
/// fact.
/// </para>
/// <para>
/// Vulkan is the default GPU tier: it runs on NVIDIA, AMD and Intel with only a normal
/// graphics driver, and skips the ~553 MB CUDA runtime download. CUDA is opt-in. CPU is the
/// fallback and is never skipped.
/// </para>
/// <para>
/// A load failure here is ordinary and recoverable. A <em>crash</em> here is not: a native
/// compiled with an AVX2 baseline can execute BMI2/AVX instructions from a static initialiser
/// and take the process down at load time on a pre-Haswell CPU, with no exception and no stack
/// trace — it presents as "the app won't launch". That is why <c>uindosill doctor</c> probes
/// each backend in a child process instead of trying them in this one.
/// </para>
/// </remarks>
public static class ParakeetNativeLibrary
{
    /// <summary>Overrides the directory searched for the native library.</summary>
    public const string DirectoryEnvironmentVariable = "UINDOSILL_PARAKEET_NATIVE_DIR";

    /// <summary>Overrides the file name of the native library.</summary>
    public const string FileNameEnvironmentVariable = "UINDOSILL_PARAKEET_LIBRARY";

    /// <summary>
    /// The exported names of upstream's <c>void pk::shutdown_backend()</c>, MSVC first, then the
    /// Itanium spelling every other compiler uses. Only the first has been seen: the three vendored
    /// v0.5.0 Windows builds all export it (they export every symbol, 2,090 of them); no
    /// non-Windows build has been inspected, so the second is what the mangling rules say and is
    /// unverified.
    /// </summary>
    internal static readonly string[] ShutdownBackendExportNames =
        ["?shutdown_backend@pk@@YAXXZ", "_ZN2pk16shutdown_backendEv"];

    private static readonly Lock Gate = new();
    private static readonly List<string> Attempts = [];
    private static bool _resolverInstalled;
    private static ComputeBackend _requestedBackend = ComputeBackend.Vulkan;
    private static bool _allowFallback = true;
    private static string? _explicitDirectory;
    private static IntPtr _handle;

    /// <summary>Path of the library that was actually loaded, once one has been.</summary>
    public static string? LoadedPath { get; private set; }

    /// <summary>
    /// Backend directory the loaded library came from — or null when it came from a flat directory
    /// or the OS search path, where its backend cannot be read off its path and is not guessed.
    /// </summary>
    public static ComputeBackend? LoadedBackend { get; private set; }

    /// <summary>Every path tried, in order. The content of a good load-failure message.</summary>
    public static IReadOnlyList<string> AttemptedPaths
    {
        get
        {
            lock (Gate)
            {
                return [.. Attempts];
            }
        }
    }

    /// <summary>
    /// Sets the backend preference and installs the resolver. Must be called before the first
    /// native call; changing the preference after a library is loaded has no effect, because a
    /// process cannot unload and reload a different build of the same library safely.
    /// </summary>
    public static void Configure(
        ComputeBackend backend = ComputeBackend.Vulkan,
        bool allowFallback = true,
        string? nativeDirectory = null)
    {
        lock (Gate)
        {
            _requestedBackend = backend;
            _allowFallback = allowFallback;
            _explicitDirectory = nativeDirectory;
            EnsureResolverInstalled();
        }
    }

    /// <summary>
    /// Loads the library if it is not loaded and checks the ABI version.
    /// </summary>
    /// <exception cref="ParakeetNativeLoadException">No candidate could be loaded.</exception>
    /// <exception cref="ParakeetAbiMismatchException">The library speaks a different ABI.</exception>
    public static int EnsureLoadedAndCompatible()
    {
        lock (Gate)
        {
            EnsureResolverInstalled();
        }

        int abi;
        try
        {
            abi = NativeMethods.parakeet_capi_abi_version();
        }
        catch (DllNotFoundException ex)
        {
            throw new ParakeetNativeLoadException(DescribeFailure(), ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new ParakeetNativeLoadException(
                $"Loaded '{LoadedPath}' but it has no parakeet_capi_abi_version export. " +
                "That file is not a parakeet.cpp C-ABI build.",
                ex);
        }

        if (abi != NativeMethods.ExpectedAbiVersion)
        {
            throw new ParakeetAbiMismatchException(NativeMethods.ExpectedAbiVersion, abi);
        }

        return abi;
    }

    /// <summary>
    /// Whether the loaded library exports <c>pk::shutdown_backend</c>: null while no library is
    /// loaded, false for a build that does not, in which case <see cref="TryShutdownBackend"/> can
    /// do nothing and a CUDA process will abort at exit exactly as before it existed.
    /// </summary>
    public static bool? ShutdownBackendAvailable
    {
        get
        {
            lock (Gate)
            {
                return _handle == IntPtr.Zero ? null : FindShutdownBackend() != IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Frees parakeet.cpp's process-global compute backend, so that a GPU backend gives its device
    /// memory back while the driver is still alive. Call once, at the end of the process's work,
    /// after every engine has been disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fix for gotcha 19. parakeet.cpp keeps one <c>pk::Backend</c> per process — the
    /// ggml backend plus a persistent device compute buffer — in a static <c>unique_ptr</c>, and
    /// on CUDA its static destructor runs at DLL unload, after the driver's own teardown, so
    /// <c>cudaFree</c> fails and ggml aborts the process with <c>0xC0000409</c>. Freeing our
    /// <c>parakeet_ctx</c> first does not help; the CLI always did that and aborted anyway.
    /// Upstream added <c>pk::shutdown_backend()</c> for precisely this and calls it after every
    /// subcommand of its own CLI. Measured on an RTX 5080, 2026-08-16: eight CUDA processes
    /// without this call all exited <c>0xC0000409</c>, sixteen with it all exited 0, and Vulkan
    /// and CPU exit 0 either way. See docs/UNPROVEN.md.
    /// </para>
    /// <para>
    /// The function is not part of the C ABI this binding is written against; it is reached
    /// through its C++-mangled export name, which every vendored build happens to carry because
    /// upstream exports every symbol. That is why this returns false rather than throwing when
    /// the export is absent — a future build could stop exporting it, and the honest outcome then
    /// is the old behaviour, reported by <see cref="ShutdownBackendAvailable"/> and by
    /// <c>uindosill doctor</c>, not a crash on the way out.
    /// </para>
    /// <para>
    /// Safe to call when nothing has been loaded (nothing happens), safe to call more than once,
    /// and upstream documents it as safe before further use — a later load recreates the backend.
    /// It takes the same mutex the compute path holds per graph, so calling it while a decode is
    /// in flight stalls until the current graph finishes rather than corrupting anything, but a
    /// decode that carries on afterwards recreates the backend, and the exit abort with it. Stop
    /// the work first.
    /// </para>
    /// </remarks>
    /// <returns>True if the library was loaded, exports the function, and it was called.</returns>
    public static unsafe bool TryShutdownBackend()
    {
        IntPtr function;
        lock (Gate)
        {
            if (_handle == IntPtr.Zero)
            {
                return false;
            }

            function = FindShutdownBackend();
        }

        if (function == IntPtr.Zero)
        {
            return false;
        }

        ((delegate* unmanaged[Cdecl]<void>)function)();
        return true;
    }

    private static IntPtr FindShutdownBackend()
    {
        foreach (var name in ShutdownBackendExportNames)
        {
            if (NativeLibrary.TryGetExport(_handle, name, out var address))
            {
                return address;
            }
        }

        return IntPtr.Zero;
    }

    private static void EnsureResolverInstalled()
    {
        if (_resolverInstalled)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
        _resolverInstalled = true;
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, NativeMethods.LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        if (LoadedPath is not null && NativeLibrary.TryLoad(LoadedPath, out var already))
        {
            return already;
        }

        lock (Gate)
        {
            Attempts.Clear();

            foreach (var (backend, directory) in CandidateDirectories())
            {
                foreach (var fileName in CandidateFileNames())
                {
                    var candidate = Path.Combine(directory, fileName);
                    Attempts.Add(candidate);

                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    if (NativeLibrary.TryLoad(candidate, out var handle))
                    {
                        LoadedPath = candidate;
                        LoadedBackend = backend;
                        _handle = handle;
                        return handle;
                    }
                }
            }

            // Last resort: let the OS loader search its own paths. Useful for a developer with
            // the library on PATH / LD_LIBRARY_PATH, never how a shipped build finds it.
            foreach (var fileName in CandidateFileNames())
            {
                Attempts.Add($"{fileName} (default loader search path)");
                if (NativeLibrary.TryLoad(fileName, assembly, searchPath, out var handle))
                {
                    LoadedPath = fileName;
                    LoadedBackend = null;
                    _handle = handle;
                    return handle;
                }
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Which backends have a library sitting on disk, without loading anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file-system question answered as one, deliberately. It says a backend's binary is
    /// <i>present</i>, never that it will load or that the machine can run it — a CUDA drop with no
    /// NVIDIA GPU behind it is present and unusable, and finding that out costs a load that can
    /// take the process down (see the remarks on this class), which is why <c>uindosill doctor</c>
    /// probes in a child process instead.
    /// </para>
    /// <para>
    /// Presence is nonetheless the right signal for choosing a <i>default</i>: the CUDA directory is
    /// exactly what the CUDA channel adds and the default channel omits, so a user who has one has
    /// gone to the trouble of a 818 MB installer to get it. Reading Velopack's channel name would
    /// answer a question about how the application was packaged; this answers the question about
    /// what it can actually reach.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ComputeBackend> BackendsPresentOnDisk()
    {
        var present = new List<ComputeBackend>();

        lock (Gate)
        {
            foreach (var backend in Enum.GetValues<ComputeBackend>())
            {
                var name = backend.ToString().ToLowerInvariant();
                var found = false;

                foreach (var root in CandidateRoots())
                {
                    foreach (var fileName in CandidateFileNames())
                    {
                        if (File.Exists(Path.Combine(root, name, fileName)))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (found)
                    {
                        break;
                    }
                }

                if (found)
                {
                    present.Add(backend);
                }
            }
        }

        return present;
    }

    /// <summary>
    /// Every directory a per-backend subdirectory could sit under, in search order. One copy,
    /// because the loader and <see cref="BackendsPresentOnDisk"/> have to agree about where a
    /// backend lives or the second will report a backend the first cannot find.
    /// </summary>
    private static List<string> CandidateRoots()
    {
        var roots = new List<string>();

        // Rooted, not taken as given. Windows resolves a native library's own imports from the
        // directory it was loaded from, but only when the path handed to LoadLibrary is absolute.
        // A relative --native-dir passes File.Exists — that resolves against the working directory
        // — and then loads without the sibling search. CUDA is the only backend that ships
        // siblings (cudart64_12.dll, cublas64_12.dll next to parakeet.dll), so it is the only one
        // this breaks, and it breaks it into a bare load failure that the loader treats as "this
        // backend is not here" before moving on to CPU without a word.
        if (_explicitDirectory is { Length: > 0 } explicitDirectory)
        {
            roots.Add(Rooted(explicitDirectory));
        }

        if (Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            roots.Add(Rooted(fromEnvironment));
        }

        var baseDirectory = AppContext.BaseDirectory;

        // Both the portable RID and the runtime's own. RuntimeInformation.RuntimeIdentifier can be
        // version-specific (it reports "ubuntu.24.04-x64" on the CI image), so a layout laid out as
        // "win-x64" would be missed if only that value were consulted.
        roots.Add(Path.Combine(baseDirectory, "native", PortableRuntimeIdentifier));
        roots.Add(Path.Combine(baseDirectory, "native", RuntimeInformation.RuntimeIdentifier));
        roots.Add(Path.Combine(baseDirectory, "native"));
        roots.Add(Path.Combine(baseDirectory, "runtimes", PortableRuntimeIdentifier, "native"));
        roots.Add(Path.Combine(baseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));
        roots.Add(baseDirectory);

        return roots;
    }

    /// <summary>
    /// The backend to use when nobody has said which: the fastest tier in <paramref name="present"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One rule, in one place, because both front ends need it and an install that disagreed with
    /// itself about which tier it runs would be worse than either answer. The window uses it for a
    /// first-run default it then remembers; the CLI uses it for a bare <c>--backend</c>.
    /// </para>
    /// <para>
    /// CUDA outranks Vulkan because its presence is not an accident: the default channel ships cpu
    /// and vulkan, and the <c>cuda</c> directory arrives only with the second, whose installer is
    /// 818 MB against 82 MB. An empty list means Vulkan rather than CPU — a build from source with
    /// no vendored natives should resolve to what it always did and let the loader say what is
    /// missing, rather than quietly settling for the slowest tier.
    /// </para>
    /// </remarks>
    public static ComputeBackend PreferredBackend(IReadOnlyList<ComputeBackend> present)
    {
        ArgumentNullException.ThrowIfNull(present);

        if (present.Contains(ComputeBackend.Cuda))
        {
            return ComputeBackend.Cuda;
        }

        if (present.Contains(ComputeBackend.Vulkan) || present.Count == 0)
        {
            return ComputeBackend.Vulkan;
        }

        return present.Contains(ComputeBackend.Cpu) ? ComputeBackend.Cpu : ComputeBackend.Vulkan;
    }

    /// <summary><see cref="PreferredBackend"/> over <see cref="BackendsPresentOnDisk"/>.</summary>
    public static ComputeBackend PreferredBackendOnDisk() => PreferredBackend(BackendsPresentOnDisk());

    private static IEnumerable<(ComputeBackend? Backend, string Directory)> CandidateDirectories()
    {
        var roots = CandidateRoots();

        foreach (var backend in BackendOrder())
        {
            var name = backend.ToString().ToLowerInvariant();
            foreach (var root in roots)
            {
                yield return (backend, Path.Combine(root, name));
            }
        }

        // A flat directory with no per-backend subdirectory: the shape a developer gets from
        // unzipping one upstream release. Which backend that library is cannot be read off its
        // path, and it is not recorded as the requested one — until 2026-08-22 it was, and a flat
        // CPU build went into the transcript's provenance as "vulkan". It is recorded as unknown.
        foreach (var root in roots)
        {
            yield return (null, root);
        }
    }

    /// <summary>
    /// The portable RID — <c>win-x64</c>, <c>linux-arm64</c> — which is what the vendoring layout
    /// in docs/NATIVE-BINARIES.md uses and what upstream names its release archives after.
    /// </summary>
    internal static string PortableRuntimeIdentifier
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win"
                : OperatingSystem.IsMacOS() ? "osx"
                : OperatingSystem.IsLinux() ? "linux"
                : "unknown";

            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            };

            return $"{os}-{architecture}";
        }
    }

    /// <summary>
    /// Absolute form of a caller-supplied directory, or the string unchanged when it cannot be
    /// rooted. A path malformed enough to defeat <see cref="Path.GetFullPath(string)"/> will fail
    /// the <c>File.Exists</c> check and be listed among the paths tried, which tells the reader
    /// more than an exception thrown out of a DllImport resolver.
    /// </summary>
    private static string Rooted(string directory)
    {
        try
        {
            return Path.GetFullPath(directory);
        }
        // IOException covers ERROR_BAD_PATHNAME and ERROR_INVALID_NAME, which Windows surfaces as a
        // plain IOException rather than an ArgumentException, and PathTooLongException derives from
        // it. Letting one escape would propagate out of a lazily-enumerated candidate list and out of
        // the DllImport resolver, which is the outcome this method exists to prevent.
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return directory;
        }
    }

    private static IEnumerable<ComputeBackend> BackendOrder()
    {
        yield return _requestedBackend;

        if (!_allowFallback)
        {
            yield break;
        }

        // Two exclusions, for two different reasons.
        //
        // Never fall back *into* CUDA: it needs its own runtime files and a supported GPU, and
        // silently landing there turns a missing-file problem into a driver problem.
        //
        // And never fall back from CUDA into Vulkan. Asking for CUDA is deliberate — it costs a
        // 553 MB runtime download to set up — so quietly substituting the other GPU tier would
        // hide the fact that the thing you went to that trouble for is not running. The chain for
        // a CUDA request is therefore CUDA then CPU, and the resulting drop from GPU speed to CPU
        // speed is loud enough to notice. Anything reading this to mean "requested, then Vulkan,
        // then CPU" is wrong for CUDA.
        if (_requestedBackend != ComputeBackend.Vulkan && _requestedBackend != ComputeBackend.Cuda)
        {
            yield return ComputeBackend.Vulkan;
        }

        if (_requestedBackend != ComputeBackend.Cpu)
        {
            yield return ComputeBackend.Cpu;
        }
    }

    private static IEnumerable<string> CandidateFileNames()
    {
        if (Environment.GetEnvironmentVariable(FileNameEnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            yield return fromEnvironment;
        }

        // Upstream has no Windows CI and publishes Windows binaries only at release-tag time, so
        // the exact file name is pinned per vendored release in docs/NATIVE-BINARIES.md rather
        // than assumed here. These are the shapes seen in the ggml family of projects.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return "parakeet.dll";
            yield return "libparakeet.dll";
            yield return "parakeet_capi.dll";
            yield return "parakeet-capi.dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "libparakeet.dylib";
            yield return "libparakeet_capi.dylib";
        }
        else
        {
            yield return "libparakeet.so";
            yield return "libparakeet_capi.so";
        }
    }

    private static string DescribeFailure()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "Could not load the parakeet.cpp native library. Vendor a pinned upstream release into the " +
            "native/<rid>/<backend> directory (see docs/NATIVE-BINARIES.md), or point " +
            $"{DirectoryEnvironmentVariable} at a directory containing it.");
        builder.AppendLine("Paths tried:");

        foreach (var attempt in AttemptedPaths)
        {
            builder.Append("  ").AppendLine(attempt);
        }

        return builder.ToString();
    }
}
