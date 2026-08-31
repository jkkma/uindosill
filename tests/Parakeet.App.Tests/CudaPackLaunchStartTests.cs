using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

/// <summary>
/// The launch that starts the CUDA pack install by itself, and the refusals that keep it quiet:
/// the wrong flavour, a recorded stop, and a machine the Settings button would be dead on anyway.
/// </summary>
/// <remarks>
/// <para>
/// <b>The start is a 1.8 GB download and is never exercised here.</b> The decision is held apart
/// from the action as <c>WouldInstallCudaPackOnLaunch</c> precisely so the suite can assert it on
/// any machine — CI's cardless runner and the maintainer's desktop alike — and only
/// configurations that must refuse ever call the start itself. The flavour comes in through the
/// same <c>backendsOnDisk</c> seam the backend-selection tests use, so none of this depends on
/// what the machine running the suite has vendored.
/// </para>
/// <para>
/// What still varies by machine is the driver probe and whether the real user-data directory
/// holds a pack, both inside <c>CanInstallCudaPack</c> — so, as in
/// <see cref="CudaPackSettingsTests"/>, the assertions hold relationships rather than values
/// wherever those two are in play.
/// </para>
/// </remarks>
public class CudaPackLaunchStartTests
{
    private static AppSettingsStore NewSettings() =>
        new(Path.Combine(TestTemp.NewDirectory("uindosill-cudapack-launch"), "settings.json"));

    private static MainWindowViewModel NewViewModel(
        AppSettingsStore settings, params ComputeBackend[] onDisk) =>
        new(new FakeEngineProvider(),
            new LocalModelStore(TestTemp.NewDirectory("uindosill-cudapack-launch")),
            ModelCatalog.Default,
            settings: settings,
            backendsOnDisk: () => onDisk);

    [Fact]
    public void TheDefaultFlavourNeverStartsByItself()
    {
        // The default channel's disk — cpu and vulkan, no cuda directory — on any machine at all,
        // an NVIDIA desktop included: its user did not choose the CUDA download, and a launch
        // that starts one anyway would be this feature reaching a channel it was refused from.
        var viewModel = NewViewModel(NewSettings(), ComputeBackend.Cpu, ComputeBackend.Vulkan);

        Assert.False(viewModel.HasCudaBackendOnDisk);
        Assert.False(viewModel.WouldInstallCudaPackOnLaunch);

        Assert.False(viewModel.InstallCudaPackOnLaunch());
        Assert.False(viewModel.IsInstallingCudaPack);
        Assert.False(viewModel.ShowCudaPackLaunchNotice);
    }

    [Fact]
    public void ARecordedStopKeepsEveryLaterLaunchQuiet()
    {
        var settings = NewSettings();
        settings.Update(current => current with { CudaPackAutoInstallDeclined = true });

        var viewModel = NewViewModel(
            settings, ComputeBackend.Cpu, ComputeBackend.Vulkan, ComputeBackend.Cuda);

        // The CUDA flavour, and still no start: the flag outranks everything else the decision
        // reads, because it is the one part of it a person put there.
        Assert.False(viewModel.WouldInstallCudaPackOnLaunch);
        Assert.False(viewModel.InstallCudaPackOnLaunch());
        Assert.False(viewModel.IsInstallingCudaPack);
    }

    [Fact]
    public void TheLaunchStartFollowsTheSettingsButtonExactly()
    {
        // On the CUDA flavour with nothing declined, the launch decision and the Settings
        // button's liveness are one question: a machine the button is dead on — no card, no
        // verified manifest, the pack already in — must not be started on, and a machine it is
        // live on is exactly the one the launch should finish setting up. Asserted as the
        // relationship because both sides genuinely vary with the machine running the suite.
        var viewModel = NewViewModel(NewSettings(), ComputeBackend.Cuda);

        Assert.Equal(viewModel.CanInstallCudaPack, viewModel.WouldInstallCudaPackOnLaunch);
    }

    [Fact]
    public void TheStopIsARefusalTheFileRemembers()
    {
        // Execute directly rather than through CanExecute — the UI's gate is the running
        // install, and what is held here is the other half of the gesture: whoever reaches Stop,
        // the refusal lands in the file the next launch reads.
        var settings = NewSettings();
        var viewModel = NewViewModel(
            settings, ComputeBackend.Cpu, ComputeBackend.Vulkan, ComputeBackend.Cuda);

        Assert.False(settings.Load().CudaPackAutoInstallDeclined);

        viewModel.StopCudaPackInstallCommand.Execute(null);

        Assert.True(settings.Load().CudaPackAutoInstallDeclined);
        Assert.False(viewModel.WouldInstallCudaPackOnLaunch);
    }

    [Fact]
    public void TheNoticeSaysNothingUntilALaunchHasStartedSomething()
    {
        // The strip above the tabs belongs to the self-started install alone. A viewmodel that
        // never started one — whatever else the machine has — must not draw it, or every launch
        // of the default channel opens under a banner about a download it will never make.
        var viewModel = NewViewModel(NewSettings(), ComputeBackend.Cpu, ComputeBackend.Vulkan);

        Assert.False(viewModel.ShowCudaPackLaunchNotice);
        Assert.Equal(string.Empty, viewModel.CudaPackLaunchNotice);
    }
}
