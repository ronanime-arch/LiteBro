using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiteBrowser;

/// <summary>Per-process memory numbers and working-set trimming.</summary>
static class Memory
{
    public static readonly int SelfId = Process.GetCurrentProcess().Id;

    const uint QueryLimited = 0x1000, SetQuota = 0x0100, VmRead = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessMemoryCountersEx2
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage,
            PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool K32EmptyWorkingSet(IntPtr process);
    [DllImport("kernel32.dll")]
    static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx2 counters, uint size);

    /// <summary>Private working set, the figure Task Manager shows as "Memory"; 0 if unknown.</summary>
    public static long PrivateWorkingSet(int pid)
    {
        var handle = OpenProcess(QueryLimited | VmRead, false, pid);
        if (handle == IntPtr.Zero) handle = OpenProcess(QueryLimited, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            var counters = new ProcessMemoryCountersEx2 { cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
            return K32GetProcessMemoryInfo(handle, ref counters, counters.cb)
                ? (long)counters.PrivateWorkingSetSize.ToUInt64() : 0;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>Moves the process's pages to the standby list; they fault back in when touched.</summary>
    public static void Trim(int pid)
    {
        var handle = OpenProcess(QueryLimited | SetQuota, false, pid);
        if (handle == IntPtr.Zero) return; // sandboxed processes may refuse
        K32EmptyWorkingSet(handle);
        CloseHandle(handle);
    }
}

/// <summary>A job whose processes all die when it is closed, including when this process crashes.</summary>
sealed class KillOnCloseJob : IDisposable
{
    const int ExtendedLimitInformation = 9;
    const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
            ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimitInformationData
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformationData info, int length);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    IntPtr handle;

    KillOnCloseJob(IntPtr handle) => this.handle = handle;

    /// <summary>Puts the process (and everything it starts later) in a new job; null if Windows refuses.</summary>
    public static KillOnCloseJob? For(Process process)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return null;
        var info = new ExtendedLimitInformationData();
        info.BasicLimitInformation.LimitFlags = KillOnJobClose;
        if (SetInformationJobObject(handle, ExtendedLimitInformation, ref info, Marshal.SizeOf(info))
            && AssignProcessToJobObject(handle, process.Handle))
            return new KillOnCloseJob(handle);
        CloseHandle(handle);
        return null;
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        TerminateJobObject(handle, 1);
        CloseHandle(handle);
        handle = IntPtr.Zero;
    }
}
