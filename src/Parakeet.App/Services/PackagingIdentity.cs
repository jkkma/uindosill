using System.Reflection;
using Parakeet.Core.Models;

namespace Parakeet.App.Services;

/// <summary>
/// What this application is called by the thing that installs it, and where that puts it.
/// </summary>
/// <remarks>
/// <para>
/// The id is read out of the assembly rather than written here, because the installer is built
/// from the MSBuild property and a second literal in C# is a second thing to keep in step. The
/// property is <c>VelopackPackageId</c> in <c>src/Parakeet.App/Parakeet.App.csproj</c>, which the
/// packaging script reads with <c>dotnet msbuild -getProperty:</c>; the csproj also emits it as
/// assembly metadata, which is what this reads back.
/// </para>
/// <para>
/// The reason any of this exists: Velopack's uninstall deletes the install root recursively, and
/// this product's weights live in <see cref="UserDataPaths.RootDirectory"/>. Those two paths must
/// never be the same directory, nor one inside the other. <c>InstallRootDirectory</c> is here so a
/// test can assert that rather than a comment claiming it.
/// </para>
/// </remarks>
public static class PackagingIdentity
{
    /// <summary>The Velopack package id the installer was built with.</summary>
    public static string PackageId { get; } =
        typeof(PackagingIdentity).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "VelopackPackageId", StringComparison.Ordinal))
            ?.Value
        ?? throw new InvalidOperationException(
            "The assembly carries no VelopackPackageId metadata. Parakeet.App.csproj sets the "
            + "property and emits the AssemblyMetadata item; one of the two has been removed.");

    /// <summary>
    /// The directory Velopack installs into on Windows, <c>%LOCALAPPDATA%\{PackageId}</c>, and the
    /// directory its uninstall removes. Computed here on every platform so a Linux test run checks
    /// the same arithmetic a Windows machine would.
    /// </summary>
    public static string InstallRootDirectory()
    {
        // The same guard UserDataPaths.RootDirectory has, and for the same reason: on a profile with
        // no local application data this would otherwise return the bare package id — a relative
        // path — and the test that holds the install root apart from the models directory would be
        // comparing two paths that are not the ones that ship.
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        return string.IsNullOrEmpty(localAppData)
            ? Path.Combine(Path.GetTempPath(), PackageId)
            : Path.Combine(localAppData, PackageId);
    }
}
