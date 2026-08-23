using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Parakeet.Engine.Python;

/// <summary>
/// A Windows job object that kills its members when the last handle to it closes — which the
/// operating system does when this process ends, however it ends.
/// </summary>
/// <remarks>
/// <para>
/// The sidecar is a child process holding 453 MiB or 1.34 GiB of weights. A host that is killed
/// from Task Manager, crashes, or is stopped by a debugger never reaches <c>DisposeAsync</c>, and
/// until 2026-08-22 the child outlived it: still resident, still reading a stdin nobody would write
/// to again, with a staged WAV beside it. The job object is the operating system's answer — one
/// per host process, created on the first sidecar start and deliberately never closed, because
/// closing it is the kill.
/// </para>
/// <para>
/// Off Windows this does nothing and says so through its return value; a host death there still
/// closes the child's stdin, which ends its loop, and nothing more is promised.
/// </para>
/// </remarks>
internal static partial class KillOnCloseJob
{
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    private static readonly object Gate = new();
    private static IntPtr _job;
    private static bool _creationFailed;

    /// <summary>
    /// Puts <paramref name="process"/> in the kill-on-close job. False when that was not possible —
    /// not Windows, or the job could not be created or joined — which is a fact to record, not a
    /// reason to refuse the sidecar.
    /// </summary>
    public static bool TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return AssignOnWindows(process);
    }

    /// <summary>True when <paramref name="process"/> is in this host's kill-on-close job.</summary>
    public static bool Contains(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return ContainsOnWindows(process);
    }

    [SupportedOSPlatform("windows")]
    private static bool AssignOnWindows(Process process)
    {
        lock (Gate)
        {
            if (_job == IntPtr.Zero && !_creationFailed)
            {
                _job = Create();
                _creationFailed = _job == IntPtr.Zero;
            }

            if (_job == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return AssignProcessToJobObject(_job, process.Handle);
            }
            catch (InvalidOperationException)
            {
                // The process is already gone; there is nothing to put in the job.
                return false;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool ContainsOnWindows(Process process)
    {
        lock (Gate)
        {
            if (_job == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return IsProcessInJob(process.Handle, _job, out var inJob) && inJob;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr Create()
    {
        var job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new JobObjectExtendedLimitInformationNative();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformationNative>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCountersNative
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformationNative
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationNative
    {
        public JobObjectBasicLimitInformationNative BasicLimitInformation;
        public IoCountersNative IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static partial IntPtr CreateJobObjectW(IntPtr securityAttributes, IntPtr name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
