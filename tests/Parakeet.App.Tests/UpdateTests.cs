using Parakeet.App.Services;
using Parakeet.App.ViewModels;

namespace Parakeet.App.Tests;

/// <summary>An <see cref="IAppUpdater"/> that records what was asked of it and reaches no network.</summary>
internal sealed class FakeUpdater : IAppUpdater
{
    public bool IsInstalled { get; set; } = true;

    public string CurrentVersion { get; set; } = "1.0.0";

    public string? Available { get; set; }

    public Exception? CheckThrows { get; set; }

    /// <summary>Held open so a test can look at the view model mid-check.</summary>
    public Task? CheckGate { get; set; }

    public Exception? DownloadThrows { get; set; }

    public int Checks { get; private set; }

    public int Downloads { get; private set; }

    public int Applies { get; private set; }

    public bool ProgressWasSupplied { get; private set; }

    public List<string> Order { get; } = [];

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        Checks++;
        Order.Add("check");

        if (CheckGate is not null)
        {
            await CheckGate;
        }

        if (CheckThrows is not null)
        {
            throw CheckThrows;
        }

        return Available;
    }

    public Task DownloadAsync(IProgress<int>? progress, CancellationToken ct)
    {
        Downloads++;
        Order.Add("download");
        ProgressWasSupplied = progress is not null;
        if (DownloadThrows is not null)
        {
            return Task.FromException(DownloadThrows);
        }

        progress?.Report(100);
        return Task.CompletedTask;
    }

    public void ApplyAndRestart()
    {
        Applies++;
        Order.Add("apply");
    }
}

public class UpdatesViewModelTests
{
    private static AppSettingsStore TempSettings() =>
        new(TestTemp.NewPath("settings.json"));

    private static UpdatesViewModel New(FakeUpdater updater, Func<Task>? shutdown = null) =>
        new(updater, TempSettings(), shutdown);

    [Fact]
    public async Task ANewerVersionBecomesAVisibleNotice()
    {
        var updater = new FakeUpdater { Available = "1.1.0", CurrentVersion = "1.0.0" };
        var viewModel = New(updater);

        await viewModel.CheckOnLaunchAsync();

        Assert.True(viewModel.IsUpdateAvailable);
        Assert.Equal("1.1.0", viewModel.AvailableVersion);
        Assert.Contains("1.1.0", viewModel.Notice, StringComparison.Ordinal);
        Assert.Contains("1.0.0", viewModel.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoNewerVersionSaysSoAndShowsNoNotice()
    {
        var updater = new FakeUpdater { Available = null };
        var viewModel = New(updater);

        await viewModel.CheckOnLaunchAsync();

        Assert.False(viewModel.IsUpdateAvailable);
        Assert.Empty(viewModel.Notice);
        Assert.Contains("Up to date", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSettingBeingOffMeansNoRequestAtAll()
    {
        // Not a request whose answer is thrown away: the one unprompted network call this product
        // makes has to actually stop being made when a user switches it off.
        var updater = new FakeUpdater { Available = "1.1.0" };
        var viewModel = New(updater);

        viewModel.CheckOnLaunch = false;
        await viewModel.CheckOnLaunchAsync();

        Assert.Equal(0, updater.Checks);
        Assert.False(viewModel.IsUpdateAvailable);
    }

    [Fact]
    public void TheSettingIsWrittenThroughImmediately()
    {
        // A preference saved only on a clean exit is a preference that does not hold.
        var settings = TempSettings();
        var viewModel = new UpdatesViewModel(new FakeUpdater(), settings);

        Assert.True(viewModel.CheckOnLaunch);
        viewModel.CheckOnLaunch = false;

        Assert.False(settings.Load().CheckForUpdatesOnLaunch);
        Assert.False(new UpdatesViewModel(new FakeUpdater(), settings).CheckOnLaunch);
    }

    [Fact]
    public async Task ACopyThatNoInstallerPutThereChecksNothing()
    {
        var updater = new FakeUpdater { IsInstalled = false, Available = "1.1.0" };
        var viewModel = New(updater);

        await viewModel.CheckOnLaunchAsync();

        Assert.Equal(0, updater.Checks);
        Assert.False(viewModel.IsSupported);
        Assert.False(viewModel.CanCheck);
        Assert.False(viewModel.CanInstall);
        Assert.Contains("not installed by the installer", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedCheckIsALineOfTextRatherThanAnException()
    {
        // Offline, rate-limited, or GitHub is down. None of it stops the product transcribing.
        var updater = new FakeUpdater { CheckThrows = new HttpRequestException("no route to host") };
        var viewModel = New(updater);

        await viewModel.CheckOnLaunchAsync();

        Assert.False(viewModel.IsUpdateAvailable);
        Assert.False(viewModel.IsBusy);
        Assert.Contains("Could not check for updates", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("no route to host", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingIsDownloadedOrAppliedWithoutTheClick()
    {
        var updater = new FakeUpdater { Available = "1.1.0" };
        var viewModel = New(updater);

        await viewModel.CheckOnLaunchAsync();

        Assert.Equal(0, updater.Downloads);
        Assert.Equal(0, updater.Applies);
    }

    [Fact]
    public async Task TheInstallOfferTurnsOnWhenACheckFindsSomethingAndOffWhileItIsWorking()
    {
        // The bug this exists for: the commands were generated with a CanExecute, which is only
        // re-queried when the command is told to and nothing was telling it — so the button was
        // disabled at construction and stayed disabled after a check found an update. Every test
        // here called ExecuteAsync directly, which does not consult CanExecute, so all of them
        // passed while the button a user has to press did nothing. Assert the bound property.
        var updater = new FakeUpdater { Available = "1.1.0" };
        var viewModel = New(updater);

        Assert.True(viewModel.CanCheck);
        Assert.False(viewModel.CanInstall);

        await viewModel.CheckOnLaunchAsync();

        Assert.True(viewModel.CanInstall);
        Assert.True(viewModel.CanCheck);
    }

    [Fact]
    public async Task NeitherOfferIsLiveWhileSomethingIsAlreadyRunning()
    {
        var gate = new TaskCompletionSource();
        var updater = new FakeUpdater { Available = "1.1.0", CheckGate = gate.Task };
        var viewModel = New(updater);

        var checking = viewModel.CheckCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CanCheck);
        Assert.False(viewModel.CanInstall);

        gate.SetResult();
        await checking;

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.CanCheck);
    }

    [Fact]
    public async Task InstallingWithNothingToInstallDoesNothing()
    {
        // The command is no longer gated by a generated CanExecute, so the guard has to be in the
        // method: a binding that fires before the notice exists must not reach the updater.
        var updater = new FakeUpdater { Available = null };
        var viewModel = New(updater);
        await viewModel.CheckOnLaunchAsync();

        await viewModel.InstallCommand.ExecuteAsync(null);

        Assert.Equal(0, updater.Downloads);
        Assert.Equal(0, updater.Applies);
    }

    [Fact]
    public async Task TheClickDownloadsAndThenRestarts()
    {
        var updater = new FakeUpdater { Available = "1.1.0" };
        var viewModel = New(updater);
        await viewModel.CheckOnLaunchAsync();

        await viewModel.InstallCommand.ExecuteAsync(null);

        Assert.Equal(1, updater.Downloads);
        Assert.Equal(1, updater.Applies);

        // The bar gets a sink. Not that it reaches a particular number here: Progress<T> posts to
        // the synchronization context it was built on, so on the UI thread it lands on the UI
        // thread and in a test with no context it lands whenever the thread pool gets to it.
        Assert.True(updater.ProgressWasSupplied);
    }

    [Fact]
    public async Task TheEngineIsShutDownBeforeTheProcessIsReplaced()
    {
        // Applying an update exits the process without a Closing event, so the backend release that
        // avoids the CUDA teardown abort (gotcha 19) has to be reached from here as well — and it
        // has to happen BEFORE the restart, which is what the ordering below asserts.
        var updater = new FakeUpdater { Available = "1.1.0" };
        var viewModel = New(updater, shutdown: () =>
        {
            updater.Order.Add("shutdown");
            return Task.CompletedTask;
        });

        await viewModel.CheckOnLaunchAsync();
        await viewModel.InstallCommand.ExecuteAsync(null);

        Assert.Equal(["check", "download", "shutdown", "apply"], updater.Order);
    }

    [Fact]
    public async Task AFailedDownloadLeavesTheInstalledCopyAlone()
    {
        var updater = new FakeUpdater
        {
            Available = "1.1.0",
            DownloadThrows = new IOException("the disk is full"),
        };
        var viewModel = New(updater);
        await viewModel.CheckOnLaunchAsync();

        await viewModel.InstallCommand.ExecuteAsync(null);

        Assert.Equal(0, updater.Applies);
        Assert.False(viewModel.IsBusy);
        Assert.Contains("could not be installed", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("the disk is full", viewModel.Status, StringComparison.Ordinal);
    }

    [Theory]
    // A release candidate looks for release candidates. Every version this project has published
    // carries a hyphen, and GitHub marks each one a prerelease, so a build that declined to search
    // them would be searching a set with nothing in it.
    [InlineData("1.0.0-rc.9", true)]
    [InlineData("1.0.0-rc.10", true)]
    [InlineData("2.1.3-beta.1", true)]
    // The prerelease label ends at the build metadata, and metadata is not a label. `1.0.0+<sha>`
    // is the shape this project's own assemblies carry, and it is stable.
    [InlineData("1.0.0-rc.9+5fb4a10", true)]
    [InlineData("1.0.0+5fb4a10e85f00e91ab5b2b8d3512c62441b8a68e", false)]
    // A stable build stays on stable, which is the whole reason this is not simply `true`: somebody
    // who installed 1.0.0 is not offered the next candidate.
    [InlineData("1.0.0", false)]
    [InlineData("2.0.1", false)]
    // Not installed, so no version and no train. A run from source behaves as it always did.
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void ABuildTracksTheTrainItIsOn(string? version, bool expected)
    {
        Assert.Equal(expected, VelopackUpdater.TracksPrereleases(version));
    }
}
