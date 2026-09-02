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
        // TestUserData spells all three names as literals because it is compiled into assemblies
        // that cannot see these constants. This is where the copies are held together.
        Assert.Equal(
            TestUserData.RootDirectory,
            Environment.GetEnvironmentVariable(UserDataPaths.DirectoryEnvironmentVariable));
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
        Assert.StartsWith(TestUserData.RootDirectory, new AppSettingsStore().Path, StringComparison.Ordinal);
        Assert.StartsWith(TestUserData.RootDirectory, new LocalModelStore().RootDirectory, StringComparison.Ordinal);

        // And the computed defaults themselves, which is the stronger statement and the one that
        // replaced an inequality here on 2026-09-01. Holding the default apart from the instance
        // said the variables were being read, but it also said the *default* still resolved to the
        // real profile — true then, and the hole a third caller fell through: the CUDA pack asks
        // UserDataPaths directly, where neither variable reached. The root is redirected now, so
        // the two paths defined against it move with it and there is no default left that names
        // somebody's own %LOCALAPPDATA%.
        Assert.StartsWith(TestUserData.RootDirectory, UserDataPaths.RootDirectory(), StringComparison.Ordinal);
        Assert.StartsWith(TestUserData.RootDirectory, AppSettingsStore.DefaultPath(), StringComparison.Ordinal);
        Assert.StartsWith(TestUserData.RootDirectory, LocalModelStore.DefaultRootDirectory(), StringComparison.Ordinal);
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
