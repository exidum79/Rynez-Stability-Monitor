using System.Runtime.InteropServices;

namespace Rynez;

/// <summary>
/// Windows kernel (kernel32/winmm) calls via P/Invoke.
/// - 1ms timer resolution (for the stutter monitor's precise timing)
/// - physical-core topology (which logical processors belong to each physical core)
/// </summary>
internal static class Native
{
    // System timer resolution (default ~15.6ms -> 1ms) so the stutter monitor's 1ms wake is precise.
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint uPeriod);

    // Console control handler: lets us catch the window's X button (CTRL_CLOSE_EVENT), logoff and
    // shutdown so we can stop y-cruncher before the OS terminates us. (Ctrl+C is handled separately
    // by .NET's CancelKeyPress, so we ignore CTRL_C here.)
    public delegate bool ConsoleCtrlHandler(uint ctrlType);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler? handler, bool add);
    public const uint CTRL_C_EVENT = 0, CTRL_BREAK_EVENT = 1, CTRL_CLOSE_EVENT = 2,
                      CTRL_LOGOFF_EVENT = 5, CTRL_SHUTDOWN_EVENT = 6;

    // Time since the last keyboard/mouse input, used to tell a real stall from one caused by the user
    // interacting (alt-tab, dismissing a screensaver, etc.) - those should not be blamed on a core.
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Milliseconds since the last keyboard/mouse input (uint.MaxValue if unavailable).</summary>
    public static uint MillisecondsSinceLastInput()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return uint.MaxValue;
        return unchecked((uint)Environment.TickCount - lii.dwTime); // both are GetTickCount-based; wrap-safe
    }

    // Keep the system awake and the display on for the duration of a test, WITHOUT changing the user's
    // power plan or screensaver settings. ES_DISPLAY_REQUIRED also suppresses the screensaver - which
    // removes the screensaver / display-off micro-freeze false positives entirely. Cleared on exit.
    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_CONTINUOUS = 0x80000000,
        ES_SYSTEM_REQUIRED = 0x00000001,
        ES_DISPLAY_REQUIRED = 0x00000002,
    }
    [DllImport("kernel32.dll")] private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

    /// <summary>Block system sleep, display-off and the screensaver until <see cref="AllowSleep"/> (or exit).</summary>
    public static void KeepSystemAndDisplayAwake() =>
        SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED | EXECUTION_STATE.ES_DISPLAY_REQUIRED);

    /// <summary>Restore normal power behaviour (release the keep-awake request).</summary>
    public static void AllowSleep() => SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);

    // Suspend / resume a whole process (all its threads) via ntdll. Used by transient / boost-cycling
    // mode to duty-cycle the y-cruncher worker on a pinned core: suspend it for a few ms, resume it for a
    // few ms, repeat - forcing the core to ramp idle->load->idle rapidly instead of sitting at a steady
    // clock. NtSuspend/ResumeProcess are undocumented but stable across every modern Windows.
    [DllImport("ntdll.dll")] private static extern uint NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")] private static extern uint NtResumeProcess(IntPtr processHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;

    // Pin the CALLING thread to a set of logical processors. The random-mode performance probe uses this:
    // it must run its fixed-work timing kernel ON the physical core under test (so what it measures is THAT
    // core's clock), so it briefly pins the duty thread to the core's logical-processor mask, measures, then
    // restores the previous affinity. SetThreadAffinityMask returns the thread's PREVIOUS mask (0 = failure).
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

    /// <summary>Pin the CURRENT OS thread to <paramref name="mask"/>. Returns the previous affinity mask
    /// (0 on failure) to hand back to <see cref="RestoreThreadAffinity"/>.</summary>
    public static ulong PinCurrentThread(ulong mask) => (ulong)SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)mask);

    /// <summary>Restore a thread affinity previously captured from <see cref="PinCurrentThread"/> (no-op if 0).</summary>
    public static void RestoreThreadAffinity(ulong previousMask)
    {
        if (previousMask != 0) SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)previousMask);
    }

    /// <summary>Open a handle to a process with suspend/resume rights (IntPtr.Zero on failure).</summary>
    public static IntPtr OpenForSuspendResume(int pid) => OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
    /// <summary>Freeze every thread of the process (no-op on a null handle).</summary>
    public static void SuspendProcess(IntPtr processHandle) { if (processHandle != IntPtr.Zero) NtSuspendProcess(processHandle); }
    /// <summary>Unfreeze every thread of the process (no-op on a null handle).</summary>
    public static void ResumeProcess(IntPtr processHandle) { if (processHandle != IntPtr.Zero) NtResumeProcess(processHandle); }
    public static void CloseProcessHandle(IntPtr h) { if (h != IntPtr.Zero) CloseHandle(h); }

    // GetLogicalProcessorInformation returns the system's core/cache/NUMA relationships.
    // We only need physical-core relationships (RelationProcessorCore = 0); each entry's ProcessorMask
    // is the bitmask of logical processors that belong to that physical core.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnLength);

    private const int RelationProcessorCore = 0;

    // Match the native struct size (32 bytes on x64): ProcessorMask(8) + Relationship(4) + pad(4) + union(16).
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION
    {
        public UIntPtr ProcessorMask;
        public int Relationship;   // 0 = physical core
        public ulong PayloadLow;   // union slot (unused)
        public ulong PayloadHigh;
    }

    /// <summary>
    /// Returns the physical cores as (core index, bitmask of its logical processors).
    /// Assumes <= 64 logical processors (single processor group) - true for all consumer Ryzen desktops.
    /// </summary>
    public static List<(int CoreIndex, ulong Mask)> GetPhysicalCores()
    {
        uint length = 0;
        GetLogicalProcessorInformation(IntPtr.Zero, ref length); // first call: get required buffer size

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformation(buffer, ref length))
                throw new InvalidOperationException("GetLogicalProcessorInformation failed");

            int structSize = Marshal.SizeOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>();
            int count = (int)length / structSize;

            var cores = new List<(int, ulong)>();
            int coreIndex = 0;
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>(buffer + i * structSize);
                if (info.Relationship == RelationProcessorCore)
                    cores.Add((coreIndex++, (ulong)info.ProcessorMask));
            }
            return cores;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
