using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rynez;

/// <summary>
/// A Windows Job Object set to KILL every assigned process when the job handle closes - which happens
/// automatically when THIS process exits: clean exit, Ctrl+C, the console window's X button, a crash,
/// or even Task Manager "End task". We assign every y-cruncher process to it, so the monitor going away
/// always takes y-cruncher down with it - no orphaned stress process left pegging the CPU.
///
/// This is the safety net. The console close handler (see Program.cs) does the graceful kill first;
/// the job guarantees no orphan even when no handler gets to run.
/// </summary>
public sealed class JobObject : IDisposable
{
    private readonly IntPtr _handle;
    public bool Ok => _handle != IntPtr.Zero;

    /// <param name="affinityMask">If non-zero, also pins EVERY process in the job to these logical
    /// processors with a hard limit that self-pinning (SetProcessAffinityMask) cannot escape - this is
    /// how single-core testing actually confines y-cruncher, which otherwise spreads to all cores.</param>
    public JobObject(ulong affinityMask = 0UL)
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) return;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        uint flags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (affinityMask != 0UL)
        {
            flags |= JOB_OBJECT_LIMIT_AFFINITY;
            info.BasicLimitInformation.Affinity = (UIntPtr)affinityMask;
        }
        info.BasicLimitInformation.LimitFlags = flags;
        int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr p = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, p, false);
            SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, p, (uint)len);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Put a process (and the children it spawns afterwards) under the job. Best-effort.</summary>
    public void Assign(Process proc)
    {
        if (_handle == IntPtr.Zero) return;
        try { AssignProcessToJobObject(_handle, proc.Handle); } catch { }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle); // closing the handle triggers KILL_ON_JOB_CLOSE
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint JOB_OBJECT_LIMIT_AFFINITY = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
