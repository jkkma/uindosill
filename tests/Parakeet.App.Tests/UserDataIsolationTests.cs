using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.Core.Models;
using Parakeet.Tests;

namespace Parakeet.App.Tests;

/// <summary>
/// The guard that keeps this suite out of the user's own files, tested like anything else.
/// </summary>
/// <remarks>
/// <see cref="TestUserData"/> is the only thing standing between a view model that defaults its
/// settings store and the settings file of whoever runs the suite, and a guard nothing exercises
/// is a guard that stops working quietly. These three say it is in force, that it names the
/// variables the product actually reads, and that the construction which leaked no longer does.
/// </remarks>
public class UserDataIsolationTests
{
    [Fact]
    public void TheRedirectNamesTheVariablesTheProductReads()
    {
        // TestUserData spells both names as literals because it is compiled into assemblies that
        // cannot see these constants. This is where the two copies are held together.
        Assert.Equal(
            TestUserData.ModelsDirectory,
            Environment.GetEnvironmentVariable(LocalModelStore.DirectoryEnvironmentVariable));
        Assert.Equal(
            TestUserData.SettingsPath,
            Environment.GetEnvironmentVariable(AppSettingsStore.PathEnvironmentVariable));
    }

    [Fact]
    public void AStoreThatWasGivenNothingCannotReachTheRealUserData()
    {
        // Both defaults, exercised the way the product exercises them — with no argument at all.
        Assert.NotEqual(AppSettingsStore.DefaultPath(), new AppSettingsStore().Path);
        Assert.NotEqual(LocalModelStore.DefaultRootDirectory(), new LocalModelStore().RootDirectory);

        Assert.StartsWith(TestUserData.RootDirectory, new AppSettingsStore().Path, StringComparison.Ordinal);
        Assert.StartsWith(TestUserData.RootDirectory, new LocalModelStore().RootDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowSavesAChosenOutputFolderIntoTheRunsOwnSettingsFile()
    {
        var directory = TestTemp.NewDirectory("uindosill-isolation");
        var settings = new AppSettingsStore(Path.Combine(directory, "settings.json"));

        // Given a store, the window saves the folder as it is chosen rather than at exit.
        var told = new MainWindowViewModel(
            new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default,
            settings: settings, player: new FakeMediaPlayer());
        told.Transcribe.OutputDirectory = directory;

        Assert.Equal(directory, settings.Load().OutputDirectory);

        // Given none — the shape that leaked — the same write lands in this run's settings file.
        // Its contents are not asserted: other test classes run alongside this one and save their
        // own folders into that same file, and racing them would be flaky rather than strict.
        var untold = new MainWindowViewModel(
            new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        untold.Transcribe.OutputDirectory = directory;

        Assert.True(File.Exists(TestUserData.SettingsPath));
    }
}
