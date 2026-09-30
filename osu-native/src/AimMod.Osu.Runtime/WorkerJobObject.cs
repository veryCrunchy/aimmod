using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AimMod.Osu.Runtime;

/// <summary>
/// Ties worker processes to the host's lifetime on Windows through a job object with
/// kill-on-close, so a crashed or killed host cannot leave workers behind.
/// </summary>
internal static class WorkerJobObject
{
    private const int extended_limit_information_class = 9;
    private const uint kill_on_job_close = 0x2000;

    private static readonly object jobLock = new();
    private static IntPtr job;
    private static bool jobAttempted;

    public static bool TryAssign(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            IntPtr handle = getOrCreateJob();
            return handle != IntPtr.Zero && AssignProcessToJobObject(handle, process.Handle);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            return false;
        }
    }

    private static IntPtr getOrCreateJob()
    {
        lock (jobLock)
        {
            if (jobAttempted)
                return job;

            jobAttempted = true;
            IntPtr created = CreateJobObjectW(IntPtr.Zero, null);
            if (created == IntPtr.Zero)
                return IntPtr.Zero;

            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = kill_on_job_close },
            };
            int length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(created, extended_limit_information_class, buffer, (uint)length))
                {
                    CloseHandle(created);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            job = created;
            return job;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
