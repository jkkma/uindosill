using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// What can be asserted about a probe whose answer is a property of the machine running it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The interesting answer cannot be tested here and that is stated rather than worked around.</b>
/// CI has no NVIDIA card, so <see cref="CudaDriverAvailability.Present"/> is unreachable in this
/// suite; a test that asserted it would either be skipped everywhere that matters or would pass by
/// asserting nothing. What is left is the part that is machine-independent — that the probe answers
/// at all rather than throwing, and that its record cannot describe a state that makes no sense —
/// and that is genuinely worth holding, because this runs behind a UI row and the failure it must
/// not have is taking a window down on a machine with an unusual driver.
/// </para>
/// <para>
/// The <c>Present</c> path was checked by hand on the RTX 5080 on 2026-08-29: <c>Present</c>, one
/// device, driver version 13040 — which is CUDA 13.4, agreeing with what <c>nvidia-smi</c> reports
/// independently. That cross-check is the evidence for this probe; the assertions below are the
/// guard rail.
/// </para>
/// </remarks>
public class CudaDriverProbeTests
{
    [Fact]
    public void ItAnswersRatherThanThrowing()
    {
        // No assertion on which answer: this suite runs on CI without a card, on the maintainer's
        // desktop with one, and on a laptop with an AMD iGPU, and all three are correct outcomes.
        var driver = CudaDriverProbe.Describe();

        Assert.NotNull(driver);
    }

    [Fact]
    public void OnlyAPresentDriverReportsDevices()
    {
        // The invariant the caller relies on to decide whether to offer a 1.8 GB download: a count
        // above zero means Present and nothing else does. An Unknown carrying a device count would
        // be a probe saying "I could not tell, and there are two of them".
        var driver = CudaDriverProbe.Describe();

        if (driver.Availability == CudaDriverAvailability.Present)
        {
            Assert.True(driver.DeviceCount > 0);
        }
        else
        {
            Assert.Equal(0, driver.DeviceCount);
        }
    }

    [Fact]
    public void TheTwoNegativeAnswersAreDistinctAndCarryNothing()
    {
        // Unknown is not a synonym for Absent — the caller is told to say nothing on Unknown rather
        // than to offer the pack, and collapsing the two would lose that. Asserted on the statics
        // because they are what the probe returns for both.
        Assert.NotEqual(CudaDriver.Unknown.Availability, CudaDriver.Absent.Availability);
        Assert.Equal(0, CudaDriver.Unknown.DeviceCount);
        Assert.Equal(0, CudaDriver.Absent.DeviceCount);
        Assert.Equal(0, CudaDriver.Unknown.DriverVersion);
        Assert.Equal(0, CudaDriver.Absent.DriverVersion);
    }

    [Fact]
    public void RepeatedProbesAgree()
    {
        // Loading and freeing nvcuda.dll twice must not change the answer. Worth holding because
        // Describe frees the library it loaded, and a probe that worked once and then reported
        // Absent would be the kind of fault that only shows on a second visit to a Settings page.
        var first = CudaDriverProbe.Describe();
        var second = CudaDriverProbe.Describe();

        Assert.Equal(first.Availability, second.Availability);
        Assert.Equal(first.DeviceCount, second.DeviceCount);
    }
}
