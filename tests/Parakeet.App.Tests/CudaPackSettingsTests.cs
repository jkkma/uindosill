using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.Core.Models;
using Parakeet.Engine.Python;

namespace Parakeet.App.Tests;

/// <summary>
/// The Settings block that offers the CUDA pack, and the two questions that decide whether it is
/// drawn at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>The answers here depend on the machine running the suite, and the assertions are written so
/// that both machines are correct.</b> CI has no NVIDIA card and the maintainer's desktop has one,
/// so <c>CanOfferCudaPack</c> is not a constant — what is constant is the *relationship* between
/// the probe, whether the pack is installed, and whether the block is shown. Asserting the
/// relationship rather than the value is what makes this a test rather than a machine detector.
/// </para>
/// <para>
/// The install itself is not exercised: it is 1.8 GB over a network, and it was driven end to end
/// by hand against a local server on 2026-08-29. What is held here is that the button is live
/// exactly where there is something to download and dead everywhere else, which since the pack was
/// uploaded is a relationship rather than the single state this build used to ship in.
/// </para>
/// </remarks>
public class CudaPackSettingsTests
{
    private static MainWindowViewModel NewViewModel() =>
        new(new FakeEngineProvider(),
            new LocalModelStore(TestTemp.NewDirectory("uindosill-cudapack")),
            ModelCatalog.Default);

    [Fact]
    public void TheBlockIsShownExactlyWhenItCouldDoSomething()
    {
        // Either the machine can use the pack and has not got it, or it already has it. A machine
        // that is neither is shown nothing, which is the whole point of the visibility binding: a
        // disabled row explaining a 1.8 GB download you can never use is an advertisement.
        var viewModel = NewViewModel();

        Assert.Equal(
            viewModel.CanOfferCudaPack || viewModel.IsCudaPackInstalled,
            viewModel.ShowCudaPack);
    }

    [Fact]
    public void ThePackIsNeverOfferedWhereItIsAlreadyInstalled()
    {
        var viewModel = NewViewModel();

        if (viewModel.IsCudaPackInstalled)
        {
            Assert.False(viewModel.CanOfferCudaPack);
        }
    }

    [Fact]
    public void TheButtonIsLiveOnlyWhereThereIsSomethingToFetch()
    {
        // Was TheButtonIsDeadWhileTheManifestIsUnverified, whose Assert.False on the shipped
        // manifest was written to start failing the day a release carried the assets. That day was
        // 2026-08-29 and v1.0.0-rc.8. What it guarded holds in both states, so it is asserted as
        // the relationship rather than as either value: the button is live only where the pinned
        // flag says the parts exist, and never merely fails when pressed.
        var viewModel = NewViewModel();

        if (!CudaPackManifest.Shipped.Verified)
        {
            Assert.False(viewModel.CanInstallCudaPack);
            Assert.False(viewModel.InstallCudaPackCommand.CanExecute(null));
            return;
        }

        // Verified, so the button follows the two questions the row itself is drawn on and nothing
        // else: a card the driver reports, and no install already running.
        Assert.Equal(
            viewModel.CanOfferCudaPack && !viewModel.IsInstallingCudaPack,
            viewModel.CanInstallCudaPack);
        Assert.Equal(
            viewModel.CanInstallCudaPack,
            viewModel.InstallCudaPackCommand.CanExecute(null));
    }

    [Fact]
    public void TheExplanationNamesTheDownloadAndSaysWhatIsUnmeasured()
    {
        var viewModel = NewViewModel();

        var text = viewModel.CudaPackExplanation;

        // Whichever state the machine is in, the block must not claim an accuracy result. The
        // 13x is a speed figure and the identical labels are an equivalence check; no DER has been
        // scored on any route, and the copy says so rather than implying the labels are better.
        Assert.Contains("13 times faster", text, StringComparison.Ordinal);
        Assert.Contains("same speakers", text, StringComparison.Ordinal);
        Assert.DoesNotContain("more accurate", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheExplanationGivesAReasonExactlyWhenThereIsOne()
    {
        var viewModel = NewViewModel();

        if (viewModel.IsCudaPackInstalled)
        {
            return;
        }

        if (CudaPackManifest.Shipped.Verified)
        {
            // The sentence retired with the flag on 2026-08-29. Left in beside a live button it
            // would tell somebody the download does not exist while offering it to them, which is
            // the same defect as a dead button with no reason, pointing the other way.
            Assert.DoesNotContain("no published download", viewModel.CudaPackExplanation,
                StringComparison.OrdinalIgnoreCase);
            return;
        }

        // A dead button with no reason beside it is the failure this guards.
        Assert.Contains("no published download", viewModel.CudaPackExplanation,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshDoesNotThrowAndLeavesTheAnswersConsistent()
    {
        // Called from the install's finally block, including after a failure, so it must be safe
        // on a machine where nothing was installed.
        var viewModel = NewViewModel();

        viewModel.RefreshCudaPack();

        Assert.Equal(
            viewModel.CanOfferCudaPack || viewModel.IsCudaPackInstalled,
            viewModel.ShowCudaPack);
    }
}
