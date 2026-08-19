using Parakeet.App.Services;
using Parakeet.Core.Models;

namespace Parakeet.App.Tests;

public class AppSettingsStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "uindosill-settings-tests", Guid.NewGuid().ToString("n"), "settings.json");

    [Fact]
    public void AMissingFileIsTheShippedDefault()
    {
        var store = new AppSettingsStore(TempFile());

        Assert.True(store.Load().CheckForUpdatesOnLaunch);
    }

    [Fact]
    public void TheSettingSurvivesARoundTrip()
    {
        var path = TempFile();
        try
        {
            Assert.True(new AppSettingsStore(path).Save(new AppSettings { CheckForUpdatesOnLaunch = false }));

            Assert.False(new AppSettingsStore(path).Load().CheckForUpdatesOnLaunch);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void AFileThatIsNotJsonIsTheShippedDefaultRatherThanAThrow()
    {
        // Whatever a hand-edited or half-written settings file contains, the window opens.
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");

            Assert.True(new AppSettingsStore(path).Load().CheckForUpdatesOnLaunch);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void AJsonFileMissingTheKeyIsTheShippedDefault()
    {
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"somethingElse\":1}");

            Assert.True(new AppSettingsStore(path).Load().CheckForUpdatesOnLaunch);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void TheSettingsFileSitsBesideTheWeightsAndNotInTheInstallDirectory()
    {
        // Same reason as the weights: a settings file under the install root is destroyed by every
        // update, so an update check the user switched off would switch itself back on.
        var settings = AppSettingsStore.DefaultPath();

        Assert.Equal(UserDataPaths.RootDirectory(), Path.GetDirectoryName(settings));
        Assert.Equal(
            Path.GetDirectoryName(LocalModelStore.DefaultRootDirectory()),
            Path.GetDirectoryName(settings));
        Assert.DoesNotContain(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            settings,
            StringComparison.Ordinal);
    }
}
