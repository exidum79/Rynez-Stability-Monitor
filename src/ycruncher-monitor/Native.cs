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
