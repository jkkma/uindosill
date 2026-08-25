using Parakeet.App.Services;
using Parakeet.Core.Models;

namespace Parakeet.App.Tests;

public class AppSettingsStoreTests
{
    private static string TempFile() =>
        TestTemp.NewPath("settings.json");

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
    public void AskThinkingIsOffAsShippedAndSurvivesARoundTrip()
    {
        var path = TempFile();
        try
        {
            Assert.False(new AppSettingsStore(path).Load().AskThinking);

            Assert.True(new AppSettingsStore(path).Save(new AppSettings { AskThinking = true }));
            Assert.True(new AppSettingsStore(path).Load().AskThinking);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void TheAskModeIsAutomaticAsShippedAndSurvivesARoundTrip()
    {
        // Automatic is the register's decision 3 router, and it ships on: the alternative is a
        // person having to know which tier answers which shape of question.
        var path = TempFile();
        try
        {
            Assert.Equal(AskModePreference.Automatic, new AppSettingsStore(path).Load().AskMode);

            Assert.True(new AppSettingsStore(path).Save(
                new AppSettings { AskMode = AskModePreference.WholeTranscript }));
            Assert.Equal(
                AskModePreference.WholeTranscript, new AppSettingsStore(path).Load().AskMode);

            Assert.True(new AppSettingsStore(path).Save(
                new AppSettings { AskMode = AskModePreference.Retrieval }));
            Assert.Equal(AskModePreference.Retrieval, new AppSettingsStore(path).Load().AskMode);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void TheOneDayOldBooleanIsHonouredOnlyWhereItCarriedAChoice()
    {
        // askWholeTranscript shipped 2026-08-25 and lived one day. A stored true was somebody
        // deliberately turning it on and becomes the fixed whole-transcript setting; a stored
        // false was the default nobody had to touch, so it carries no choice and becomes
        // Automatic rather than pinning a user to retrieval on a value they never set.
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            File.WriteAllText(path, "{\"askWholeTranscript\":true}");
            Assert.Equal(
                AskModePreference.WholeTranscript, new AppSettingsStore(path).Load().AskMode);

            File.WriteAllText(path, "{\"askWholeTranscript\":false}");
            Assert.Equal(AskModePreference.Automatic, new AppSettingsStore(path).Load().AskMode);

            // And the new name wins wherever both are present.
            File.WriteAllText(path, "{\"askWholeTranscript\":true,\"askMode\":\"retrieval\"}");
            Assert.Equal(AskModePreference.Retrieval, new AppSettingsStore(path).Load().AskMode);

            // An unreadable name degrades to as-shipped, like every other setting here.
            File.WriteAllText(path, "{\"askMode\":\"whatever\"}");
            Assert.Equal(AskModePreference.Automatic, new AppSettingsStore(path).Load().AskMode);
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
