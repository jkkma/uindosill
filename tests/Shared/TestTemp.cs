namespace Parakeet.Tests;

/// <summary>
/// The one directory a test assembly's run scratches in, and the only one it leaves behind — which
/// is to say, none, because it is deleted when the process ends.
/// </summary>
/// <remarks>
/// <para>
/// Tests wrote their scratch directories straight into the system temporary directory with
/// <c>Directory.CreateTempSubdirectory</c> and almost never deleted them, because the shape that
/// made them convenient is the shape that makes cleanup awkward: a helper returns
/// <c>(ViewModel, string Directory)</c> and the directory outlives the method that created it, so
/// there is nowhere to put a <c>using</c>. Nothing failed and nothing reported it. By 2026-08-25 a
/// development machine held 16,465 of them.
/// </para>
/// <para>
/// One root per process fixes that without asking any test to own a lifetime: every allocation
/// here is a child of <see cref="RootDirectory"/>, and the root goes at process exit whether or
/// not anything was tidy. <see cref="TempDirectory"/> still exists for the tests that want their
/// directory gone at a known moment rather than at the end, and it allocates from the same root,
/// so a missed <c>Dispose</c> is a delayed cleanup rather than a lost one.
/// </para>
/// <para>
/// Compiled into every test project by <c>tests/Directory.Build.props</c>, beside
/// <see cref="TestUserData"/>, which takes its redirect directory from here.
/// </para>
/// </remarks>
internal static class TestTemp
{
    private static int _allocations;

    /// <summary>The run's own directory under the system temporary directory.</summary>
    internal static string RootDirectory { get; } = CreateRoot();

    /// <summary>
    /// A new, empty directory named after <paramref name="prefix"/>. The name is the caller's
    /// prefix and a counter rather than a GUID, because the one time anybody reads these names is
    /// while a failing test still has its directory open, and `uindosill-vm-12` says more than
    /// thirty-two hex digits do.
    /// </summary>
    internal static string NewDirectory(string prefix)
    {
        var path = Path.Combine(
            RootDirectory,
            $"{prefix}-{Interlocked.Increment(ref _allocations)}");

        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A path to <paramref name="fileName"/> inside a new, empty directory. The file is not
    /// created — callers are usually about to write it, or about to prove that its absence is
    /// handled.
    /// </summary>
    internal static string NewPath(string fileName) =>
        Path.Combine(NewDirectory(Path.GetFileNameWithoutExtension(fileName)), fileName);

    private static string CreateRoot()
    {
        var root = Directory.CreateTempSubdirectory("uindosill-tests-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Remove(root);
        return root;
    }

    /// <summary>
    /// Best effort, and deliberately not a failure: a leftover temporary directory is not worth
    /// failing a run that has already passed, and a file still held open would do exactly that.
    /// </summary>
    internal static void Remove(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A scratch directory that goes at the end of the block rather than the end of the run.
/// </summary>
/// <remarks>
/// Worth keeping beside <see cref="TestTemp.NewDirectory"/> for the tests that assert about a
/// directory's contents and want the next one clean, and for the handful that prove something
/// about removal. It allocates from the same root, so a <c>Dispose</c> that never runs costs a
/// directory until the process ends rather than for ever.
/// </remarks>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Path = TestTemp.NewDirectory("temp");

    public string Path { get; }

    public void Dispose() => TestTemp.Remove(Path);
}
