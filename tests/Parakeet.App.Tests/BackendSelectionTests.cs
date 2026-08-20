using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

/// <summary>
/// Which compute backend the window starts on, and that the answer survives a restart.
/// </summary>
/// <remarks>
/// <para>
/// Two defects, one fix. The application defaulted to Vulkan unconditionally, so the CUDA channel —
/// an 818 MB installer against 82 MB, taken deliberately — started on the slower of the two GPU
/// tiers, 0.0110 real-time factor against CUDA's 0.0064 on the desktop. And nothing was persisted,
/// so a user who noticed and switched had to switch again on every launch.
/// </para>
/// <para>
/// Every test here passes its own settings path and its own list of on-disk backends. Neither is
/// optional politeness: the store's default path is the real one under the user's profile, and the
/// probe's default reads whatever the test host happens to have unpacked beside it.
/// </para>
/// </remarks>
public class BackendSelectionTests
{
    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "uindosill-backend-tests", Guid.NewGuid().ToString("n"), "settings.json");

    private static MainWindowViewModel NewViewModel(
        AppSettingsStore settings, params ComputeBackend[] onDisk) =>
        new(new FakeEngineProvider(),
            new LocalModelStore(Directory.CreateTempSubdirectory("uindosill-backend").FullName),
            ModelCatalog.Default,
            updater: null,
            settings: settings,
            backendsOnDisk: () => onDisk);

    // --------------------------------------------------------------- the default

    [Theory]
    [InlineData(ComputeBackend.Cuda)]                          // the CUDA channel, on its own
    [InlineData(ComputeBackend.Cpu, ComputeBackend.Vulkan, ComputeBackend.Cuda)]
    public void CudaOnDiskIsTheDefaultBecauseNobodyGetsItByAccident(params ComputeBackend[] onDisk) =>
        Assert.Equal(ComputeBackend.Cuda, MainWindowViewModel.BestBackendPresent(onDisk));

    [Fact]
    public void TheDefaultChannelStartsOnVulkan() =>
        Assert.Equal(
            ComputeBackend.Vulkan,
            MainWindowViewModel.BestBackendPresent([ComputeBackend.Cpu, ComputeBackend.Vulkan]));

    [Fact]
    public void ACpuOnlyDropStartsOnCpu() =>
        // Not Vulkan-and-let-it-fall-back. The dropdown should say what will actually run.
        Assert.Equal(ComputeBackend.Cpu, MainWindowViewModel.BestBackendPresent([ComputeBackend.Cpu]));

    [Fact]
    public void NothingOnDiskStillSaysVulkan() =>
        // A build from source with no vendored natives, which is every developer's first run and
        // what shipped before any of this. The loader's own message is the right place to find out.
        Assert.Equal(ComputeBackend.Vulkan, MainWindowViewModel.BestBackendPresent([]));

    // ------------------------------------------------------------- the persistence

    [Fact]
    public void AStoredChoiceBeatsWhatIsOnDisk()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = new AppSettingsStore(path);
            Assert.True(settings.Save(new AppSettings { Backend = ComputeBackend.Cpu }));

            // CPU chosen with CUDA sitting right there is not a mistake to correct. Somebody whose
            // GPU path misbehaves said something, and reinstating the GPU under them on the next
            // launch would be the setting mattering least exactly when it matters most.
            var viewModel = NewViewModel(settings, ComputeBackend.Cpu, ComputeBackend.Vulkan, ComputeBackend.Cuda);
            Assert.Equal(ComputeBackend.Cpu, viewModel.Backend);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void WithNothingStoredTheBestOnDiskWins()
    {
        var path = TempSettingsPath();
        try
        {
            var viewModel = NewViewModel(new AppSettingsStore(path), ComputeBackend.Cpu, ComputeBackend.Cuda);
            Assert.Equal(ComputeBackend.Cuda, viewModel.Backend);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ChoosingABackendRemembersIt()
    {
        var path = TempSettingsPath();
        try
        {
            var first = NewViewModel(new AppSettingsStore(path), ComputeBackend.Vulkan);
            Assert.Equal(ComputeBackend.Vulkan, first.Backend);
            first.Backend = ComputeBackend.Cpu;

            Assert.Equal(ComputeBackend.Cpu, new AppSettingsStore(path).Load().Backend);

            var second = NewViewModel(new AppSettingsStore(path), ComputeBackend.Vulkan);
            Assert.Equal(ComputeBackend.Cpu, second.Backend);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void StartingUpWritesNothing()
    {
        var path = TempSettingsPath();
        try
        {
            var viewModel = NewViewModel(new AppSettingsStore(path), ComputeBackend.Cuda);

            // Reading a default is not choosing one. If construction wrote the file, the picked
            // default would harden into a stored choice, and a later release that picks better
            // would be overruled by a decision the user never made.
            Assert.False(File.Exists(path), "constructing the window wrote a settings file");
        }
        finally
        {
            Cleanup(path);
        }
    }

    // --------------------------------------------------------------- the settings file

    [Fact]
    public void TheBackendSurvivesARoundTripAsAName()
    {
        var path = TempSettingsPath();
        try
        {
            Assert.True(new AppSettingsStore(path).Save(new AppSettings { Backend = ComputeBackend.Cuda }));

            // The name, not the enum's number. Reordering ComputeBackend must not silently turn one
            // user's stored CUDA into Vulkan.
            Assert.Contains("\"cuda\"", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal(ComputeBackend.Cuda, new AppSettingsStore(path).Load().Backend);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void NoChoiceIsAbsentFromTheFileRatherThanNull()
    {
        var path = TempSettingsPath();
        try
        {
            Assert.True(new AppSettingsStore(path).Save(new AppSettings()));

            Assert.DoesNotContain("backend", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Null(new AppSettingsStore(path).Load().Backend);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void AnUnknownBackendNameReadsAsNoChoice()
    {
        var path = TempSettingsPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{"checkForUpdatesOnLaunch":true,"backend":"metal"}""");

            // A future backend read by an older build, or a hand-edited file. Same rule as the rest
            // of the store: degrade to as-shipped, never throw and never guess CPU.
            Assert.Null(new AppSettingsStore(path).Load().Backend);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void UpdateKeepsTheSettingItWasNotAskedAbout()
    {
        var path = TempSettingsPath();
        try
        {
            var store = new AppSettingsStore(path);
            Assert.True(store.Update(current => current with { Backend = ComputeBackend.Cuda }));
            Assert.True(store.Update(current => current with { CheckForUpdatesOnLaunch = false }));

            // The bug Update exists to stop: Save(new AppSettings { OneField = value }) compiles,
            // reads back correctly, and resets every other field to its default on the way.
            var loaded = store.Load();
            Assert.Equal(ComputeBackend.Cuda, loaded.Backend);
            Assert.False(loaded.CheckForUpdatesOnLaunch);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void TurningOffTheUpdateCheckDoesNotForgetTheBackend()
    {
        var path = TempSettingsPath();
        try
        {
            var store = new AppSettingsStore(path);
            Assert.True(store.Save(new AppSettings { Backend = ComputeBackend.Cuda }));

            // Through the view model that owns the switch, because that is the call site that had
            // the bug: it built a fresh AppSettings for one field.
            var updates = new UpdatesViewModel(new NotInstalledUpdater(), store) { CheckOnLaunch = false };
            Assert.False(updates.CheckOnLaunch);

            Assert.Equal(ComputeBackend.Cuda, store.Load().Backend);
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static void Cleanup(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
