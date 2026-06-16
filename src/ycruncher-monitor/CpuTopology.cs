namespace Rynez;

/// <summary>
/// Detects and holds the CPU's physical-core layout.
/// For each physical core it also keeps the bitmask of logical processors that belong to it.
/// </summary>
public sealed class CpuTopology
{
    public IReadOnlyList<PhysicalCore> Cores { get; }
    public int LogicalCount { get; }
    public bool SmtEnabled { get; }

    public CpuTopology()
    {
        var raw = Native.GetPhysicalCores();
        Cores = raw.Select(c => new PhysicalCore(c.CoreIndex, c.Mask)).ToList();
        LogicalCount = Environment.ProcessorCount;
        // More logical processors than physical cores means SMT (hyper-threading style) is enabled.
        SmtEnabled = LogicalCount > Cores.Count;
    }

    /// <summary>OS logical-processor number -> the physical-core index it belongs to (0-based). -1 if not found.
    /// (Exact reverse mapping via the real topology mask, not a /2 guess.)</summary>
    public int CoreOfLogical(int logical)
    {
        if (logical < 0 || logical > 63) return -1;
        ulong bit = 1UL << logical;
        foreach (var c in Cores)
            if ((c.Mask & bit) != 0) return c.Index;
        return -1;
    }

    public void PrintSummary()
    {
        Console.WriteLine($"  Physical cores: {Cores.Count} / Logical processors: {LogicalCount} / SMT: {(SmtEnabled ? "ON" : "OFF")}");
    }
}

/// <summary>A single physical core. Mask is the bitmask of logical processors belonging to it.</summary>
public sealed record PhysicalCore(int Index, ulong Mask);
