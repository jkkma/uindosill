using System.Runtime.InteropServices;

namespace Parakeet.Engine.Python;

/// <summary>Whether this machine's driver can run CUDA, for the one decision that turns on it.</summary>
public enum CudaDriverAvailability
{
    /// <summary>
    /// The question was not answered — the library loaded but a call failed in a way that is not
    /// "no device". <b>Not a synonym for absent</b>, following <c>GpuClass.Unknown</c>: the caller
    /// decides what an unanswered question is worth, and for this one the answer is to say nothing
    /// rather than to offer a 1.8 GB download on a guess.
    /// </summary>
    Unknown = 0,

    /// <summary>No CUDA driver: the library is not there, or it enumerated no device.</summary>
    Absent = 1,

    /// <summary>At least one CUDA device, which is what makes the pack worth downloading.</summary>
    Present = 2,
}

/// <summary>What the CUDA driver reported.</summary>
/// <param name="Availability">Per <see cref="CudaDriverAvailability"/>.</param>
/// <param name="DeviceCount">Devices enumerated, and zero for every answer but Present.</param>
/// <param name="DriverVersion">
/// The driver's CUDA version as the driver API reports it — 13000 for 13.0 — or zero when it could
/// not be asked. Carried because it is the one fact that decides whether a pack built against a
/// given CUDA toolkit can run here at all, and because a run report that names a card without
/// naming its driver cannot be reproduced.
/// </param>
public sealed record CudaDriver(CudaDriverAvailability Availability, int DeviceCount, int DriverVersion)
{
    /// <summary>The answer when the driver could not be asked at all.</summary>
    public static CudaDriver Unknown { get; } = new(CudaDriverAvailability.Unknown, 0, 0);

    /// <summary>The answer when there is no CUDA here.</summary>
    public static CudaDriver Absent { get; } = new(CudaDriverAvailability.Absent, 0, 0);
}

/// <summary>
/// Asks the NVIDIA display driver whether CUDA can run here, so the application can decide whether
/// the CUDA pack is worth offering.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this cannot be asked of the bundle.</b> The obvious probe is
/// <c>torch.cuda.is_available()</c>, and it is useless for this question: the bundle pins the CPU
/// torch build, which answers <c>False</c> on a machine with four cards in it. The thing being
/// decided is whether to <i>install</i> the CUDA build, so the probe has to work before it exists —
/// which puts it on this side of the process boundary and out of Python's reach.
/// </para>
/// <para>
/// <b>`nvcuda.dll`, not the adapter name.</b> The display driver installs the CUDA driver API
/// alongside itself, so its presence answers "can this machine run CUDA" rather than "is there a
/// card with NVIDIA in its name" — which is the question, and which a WMI adapter query gets wrong
/// in both directions: an NVIDIA adapter with a driver too old for the pack, and an enumerated
/// adapter on a machine where the driver was removed.
/// </para>
/// <para>
/// <b>The device is counted rather than the library merely loaded</b>, on the same principle as
/// <c>placement.py</c>: a library that loads is registration, and registration is not capability.
/// A driver DLL left behind by an uninstall loads and enumerates nothing, and offering somebody a
/// 1.8 GB download on the strength of a stale file is exactly the failure this counts to avoid.
/// </para>
/// <para>
/// <b>Nothing here initialises a context or allocates on the device.</b> <c>cuInit</c> is the
/// documented prerequisite for enumeration and is what this pays; it is cheap and it is not a
/// context. This runs on the Settings page rather than at launch all the same, because the driver's
/// first initialisation is not free and most users never open it.
/// </para>
/// </remarks>
public static class CudaDriverProbe
{
    private const string Library = "nvcuda.dll";

    /// <summary>`CUDA_SUCCESS`. Every other value is a failure this treats as an unanswered question.</summary>
    private const int CudaSuccess = 0;

    /// <summary>
    /// `CUDA_ERROR_NO_DEVICE`. The one failure that is an *answer* rather than an unknown: the
    /// driver is here, it worked, and it found nothing.
    /// </summary>
    private const int CudaErrorNoDevice = 100;

    /// <summary>
    /// `CUDA_ERROR_SYSTEM_DRIVER_MISMATCH` and `CUDA_ERROR_INSUFFICIENT_DRIVER`, both of which mean
    /// a driver that cannot serve this API however present it is — an answer, not an unknown.
    /// </summary>
    private const int CudaErrorInsufficientDriver = 35;
    private const int CudaErrorSystemDriverMismatch = 803;

    private delegate int CuInit(uint flags);

    private delegate int CuDeviceGetCount(out int count);

    private delegate int CuDriverGetVersion(out int version);

    /// <summary>
    /// Asks the driver, answering <see cref="CudaDriver.Unknown"/> rather than throwing.
    /// </summary>
    /// <remarks>
    /// Every failure is caught. This is a probe behind an optional download, and there is no
    /// failure of it worth taking a window down for: the product without the pack is the product
    /// as it ships.
    /// </remarks>
    public static CudaDriver Describe()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The pack is a win-x64 artefact and nothing else is built. Answering Absent rather
            // than Unknown because this is settled rather than unasked.
            return CudaDriver.Absent;
        }

        nint handle = 0;
        try
        {
            if (!NativeLibrary.TryLoad(Library, out handle))
            {
                return CudaDriver.Absent;
            }

            if (!TryGet<CuInit>(handle, "cuInit", out var init)
                || !TryGet<CuDeviceGetCount>(handle, "cuDeviceGetCount", out var getCount))
            {
                // The library is here and does not export the driver API. That is not a machine
                // without CUDA, it is a machine whose answer this cannot read.
                return CudaDriver.Unknown;
            }

            var initResult = init(0);
            if (initResult is CudaErrorNoDevice or CudaErrorInsufficientDriver or CudaErrorSystemDriverMismatch)
            {
                return CudaDriver.Absent;
            }

            if (initResult != CudaSuccess)
            {
                return CudaDriver.Unknown;
            }

            if (getCount(out var count) != CudaSuccess)
            {
                return CudaDriver.Unknown;
            }

            if (count <= 0)
            {
                return CudaDriver.Absent;
            }

            // Best effort, and deliberately not fatal: a device count is the decision and the
            // version is provenance. A driver that enumerates a card and refuses to name its
            // version is still a driver worth offering the pack to.
            var version = 0;
            if (TryGet<CuDriverGetVersion>(handle, "cuDriverGetVersion", out var getVersion))
            {
                _ = getVersion(out version);
            }

            return new CudaDriver(CudaDriverAvailability.Present, count, version);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
                                              or BadImageFormatException or MarshalDirectiveException)
        {
            return CudaDriver.Unknown;
        }
        finally
        {
            if (handle != 0)
            {
                NativeLibrary.Free(handle);
            }
        }
    }

    private static bool TryGet<T>(nint handle, string name, out T function) where T : Delegate
    {
        if (NativeLibrary.TryGetExport(handle, name, out var address) && address != 0)
        {
            function = Marshal.GetDelegateForFunctionPointer<T>(address);
            return true;
        }

        function = null!;
        return false;
    }
}
