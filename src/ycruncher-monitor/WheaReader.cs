using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;

namespace Rynez;

/// <summary>Which hardware domain a WHEA hardware-error event belongs to.</summary>
public enum WheaDomain { MemoryImc, CpuCore, PciePlatform, Unknown }

/// <summary>One decoded WHEA-Logger hardware-error event.</summary>
public sealed record WheaEvent(
    DateTime TimeUtc, long RecordId, int EventId, WheaDomain Domain,
    bool Corrected, int? Bank, string? Unit, int? ApicId, string Summary);

/// <summary>
/// Reads Microsoft-Windows-WHEA-Logger events from the Windows System event log and classifies
/// each as a MEMORY/IMC vs CPU-CORE vs PCIe/platform hardware error.
///
/// This is the "hardware-level attribution" complement to the symptom-level detectors: the CPU's
/// Machine Check Architecture (MCA) records which unit faulted in a per-bank fashion. On AMD AM5
/// (Zen 4/5), MCA banks 16/17 are the UMC (Unified Memory Controller) - those, plus the dedicated
/// memory-error event IDs, mean the fault is in the RAM/IMC domain, not the CPU core.
///
/// Hard caveat (non-ECC DDR5): a "DRAM ECC error" only reaches host MCA/WHEA when ECC is active.
/// On consumer non-ECC DDR5 a RAM-overclock fault is usually silently on-die-corrected or just
/// corrupts/reboots, so it never appears here. Absence of WHEA memory events does NOT clear memory.
/// </summary>
public sealed class WheaReader
{
    // AMD Zen MCA bank -> rough unit name. Only 16/17 (UMC) are treated as the memory domain; the
    // rest are best-effort hints - the reliable signal is "memory vs not", not the exact unit.
    private static readonly Dictionary<int, string> ZenBankHint = new()
    {
        [0] = "LS (load/store)", [1] = "IF (instruction fetch)", [2] = "L2", [3] = "DE (decode)",
        [5] = "EX (execution)", [6] = "FP (floating point)", [7] = "L3",
        [16] = "UMC0 (memory controller)", [17] = "UMC1 (memory controller)",
    };

    private long _lastRecordId;
    public bool Available { get; private set; } = true;
    public string? LastError { get; private set; }

    private const string Query = "*[System/Provider/@Name='Microsoft-Windows-WHEA-Logger']";

    /// <summary>Establish a baseline so only events logged AFTER this point are reported as new.</summary>
    public WheaReader()
    {
        try
        {
            var q = new EventLogQuery("System", PathType.LogName, Query) { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            using var newest = reader.ReadEvent();
            _lastRecordId = newest?.RecordId ?? 0;
        }
        catch (Exception ex) { Available = false; LastError = ex.Message; }
    }

    /// <summary>Return WHEA events logged since the previous poll (chronological order).</summary>
    public List<WheaEvent> PollNew()
    {
        var result = new List<WheaEvent>();
        if (!Available) return result;
        try
        {
            var q = new EventLogQuery("System", PathType.LogName, Query) { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            for (EventRecord? rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
            {
                using (rec)
                {
                    long rid = rec.RecordId ?? 0;
                    if (rid <= _lastRecordId) break; // reverse order (newest first): reached seen events
                    var ev = Parse(rec, rid);
                    if (ev != null) result.Add(ev);
                }
            }
        }
        catch (Exception ex) { Available = false; LastError = ex.Message; }
        if (result.Count > 0)
        {
            _lastRecordId = Math.Max(_lastRecordId, result[0].RecordId); // first = newest
            result.Reverse(); // hand back oldest -> newest
        }
        return result;
    }

    private static WheaEvent? Parse(EventRecord rec, long rid)
    {
        int id = rec.Id;
        DateTime t = (rec.TimeCreated ?? DateTime.Now).ToUniversalTime();
        var d = ReadNamedData(rec);
        int? bank = TryInt(d, "MCABank") ?? TryInt(d, "Bank");
        int? apic = TryInt(d, "ApicId");
        bool corrected = rec.Level == 3 /*Warning*/ || id is 17 or 19 or 21 or 23 or 25 or 27 or 41 or 43 or 45 or 47 or 49 or 2;

        WheaDomain domain;
        string? unit = null;
        string summary;
        switch (id)
        {
            case 22: case 23: case 46: case 47: // platform memory error (decoded DRAM: rank/bank/row/col)
            case 48: case 49:                   // memory error (UMC)
                domain = WheaDomain.MemoryImc;
                unit = "DRAM/UMC";
                summary = MemorySummary(d);
                break;
            case 18: case 19: case 20: case 21: // processor machine-check (x64)
                if (bank is 16 or 17) { domain = WheaDomain.MemoryImc; unit = ZenBankHint.GetValueOrDefault(bank.Value, "UMC"); }
                else { domain = WheaDomain.CpuCore; unit = bank is int b ? ZenBankHint.GetValueOrDefault(b, $"core/cache (bank {b})") : "core/cache"; }
                summary = ProcSummary(d, bank, apic, unit);
                break;
            case 16: case 17: case 40: case 41:                   // PCI Express
            case 24: case 25: case 42: case 43:                   // PCI/PCI-X bus
            case 26: case 27: case 44: case 45:                   // PCI device
                domain = WheaDomain.PciePlatform;
                unit = "PCIe/IO";
                summary = $"PCIe/IO hardware error (event {id})";
                break;
            default:
                domain = WheaDomain.Unknown;
                summary = $"WHEA event {id}";
                break;
        }
        return new WheaEvent(t, rid, id, domain, corrected, bank, unit, apic, summary);
    }

    private static string ProcSummary(Dictionary<string, string> d, int? bank, int? apic, string? unit)
    {
        var parts = new List<string> { $"processor MCA bank {bank?.ToString() ?? "?"} [{unit}]" };
        if (apic.HasValue) parts.Add($"ApicId {apic}");
        if (d.TryGetValue("MciStat", out var s)) parts.Add($"MCi_STATUS {s}");
        if (d.TryGetValue("MciAddr", out var a)) parts.Add($"addr {a}");
        return string.Join(", ", parts);
    }

    private static string MemorySummary(Dictionary<string, string> d)
    {
        var parts = new List<string> { "DRAM/UMC memory error" };
        foreach (var k in new[] { "PhysicalAddress", "Channel", "RankNumber", "Bank", "Row", "Column", "MciStatus", "MciStat" })
            if (d.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) parts.Add($"{k} {v}");
        return string.Join(", ", parts);
    }

    /// <summary>Flatten the event's EventData/UserData payload into a name -> value map.</summary>
    private static Dictionary<string, string> ReadNamedData(EventRecord rec)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = XDocument.Parse(rec.ToXml());
            foreach (var container in doc.Descendants().Where(e => e.Name.LocalName is "EventData" or "UserData"))
                foreach (var el in container.Descendants().Where(e => !e.HasElements))
                {
                    string? name = el.Attribute("Name")?.Value;
                    if (name == null)
                    {
                        if (el.Name.LocalName == "Data") continue; // unnamed <Data> - skip
                        name = el.Name.LocalName;                  // UserData leaf element named after the field
                    }
                    if (!string.IsNullOrEmpty(name)) dict[name] = el.Value;
                }
        }
        catch { /* best-effort: a parse failure just yields fewer fields */ }
        return dict;
    }

    private static int? TryInt(Dictionary<string, string> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        try
        {
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return (int)Convert.ToInt64(v[2..], 16);
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return n;
        }
        catch { }
        return null;
    }
}
