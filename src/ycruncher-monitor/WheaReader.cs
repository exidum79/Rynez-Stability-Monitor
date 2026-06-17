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
/// Classification is driven by the error record itself, not by hardcoded bank numbers:
///   1. Primary  - parse the raw UEFI CPER record (the "RawData" blob) and read each section's
///                 SectionType GUID. Memory / processor / PCIe section GUIDs are vendor- and
///                 machine-independent (defined by UEFI), so this works regardless of how many
///                 cores / memory channels / PCIe slots a system has.
///   2. Fallback - when no CPER blob is present (e.g. the translated "memory error" events), use
///                 the OS-defined event ID, which already encodes memory vs processor vs PCIe.
/// The MCA bank number is only used as a cosmetic unit hint in the summary, never to decide memory
/// vs core.
///
/// Hard caveat (non-ECC DDR5): a DRAM error only reaches host MCA/WHEA when reportable ECC is
/// active. Consumer non-ECC DDR5 only has silent on-die ECC, so a RAM-overclock fault is corrected
/// invisibly or just corrupts/reboots and never appears here. Absence of WHEA memory events does
/// NOT clear memory; that needs ECC DIMMs on an ECC-reporting board.
/// </summary>
public sealed class WheaReader
{
    // UEFI CPER section-type GUIDs (machine-independent).
    private static readonly Guid SecMemory  = new("a5bc1114-6f64-4ede-b863-3e83ed7c83b1");
    private static readonly Guid SecMemory2 = new("61ec04fc-48e6-d813-25c9-8daa44750b12");
    private static readonly Guid SecProcGen = new("9876ccad-47b4-4bdb-b65e-16f193c4f3db");
    private static readonly Guid SecProcX64 = new("dc3ea0b0-a144-4797-b95b-53fa242b6e1d");
    private static readonly Guid SecPcie    = new("d995e954-bbc1-430f-ad91-b44dcb3c6f35");
    private static readonly Guid SecPciBus  = new("c5753963-3b84-4095-bf78-eddad3f9c9dd");
    private static readonly Guid SecPciDev  = new("eb5e4685-ca66-4769-b6a2-26068b001326");

    // Cosmetic AMD Zen MCA bank -> unit hint for the summary ONLY. Does not drive classification.
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

        // 1) Primary: classify from the raw CPER record's section GUIDs.
        WheaDomain domain = WheaDomain.Unknown;
        byte[]? raw = GetBytes(d, "RawData");
        if (raw != null) domain = ClassifyFromCper(raw);

        // 2) Fallback: OS-defined event ID (covers events with no CPER blob, e.g. 48/49).
        if (domain == WheaDomain.Unknown)
            domain = id switch
            {
                22 or 23 or 46 or 47 or 48 or 49 => WheaDomain.MemoryImc,
                18 or 19 or 20 or 21 => (bank is 16 or 17) ? WheaDomain.MemoryImc : WheaDomain.CpuCore,
                16 or 17 or 40 or 41 or 24 or 25 or 26 or 27 or 42 or 43 or 44 or 45 => WheaDomain.PciePlatform,
                _ => WheaDomain.Unknown,
            };

        string? unit;
        string summary;
        switch (domain)
        {
            case WheaDomain.MemoryImc:
                unit = "DRAM/UMC";
                summary = MemorySummary(d);
                break;
            case WheaDomain.CpuCore:
                unit = bank is int b ? ZenBankHint.GetValueOrDefault(b, $"core/cache (bank {b})") : "core/cache";
                summary = ProcSummary(d, bank, apic, unit);
                break;
            case WheaDomain.PciePlatform:
                unit = "PCIe/IO";
                summary = $"PCIe/IO hardware error (event {id})";
                break;
            default:
                unit = null;
                summary = $"WHEA event {id}";
                break;
        }
        return new WheaEvent(t, rid, id, domain, corrected, bank, unit, apic, summary);
    }

    /// <summary>Parse a UEFI CPER record and return the domain from its section-type GUIDs.</summary>
    private static WheaDomain ClassifyFromCper(byte[] b)
    {
        try
        {
            if (b.Length < 128) return WheaDomain.Unknown;
            if (b[0] != (byte)'C' || b[1] != (byte)'P' || b[2] != (byte)'E' || b[3] != (byte)'R') return WheaDomain.Unknown;
            int sectionCount = BitConverter.ToUInt16(b, 0x0A);
            if (sectionCount <= 0 || sectionCount > 64) return WheaDomain.Unknown;
            bool mem = false, proc = false, pcie = false;
            for (int i = 0; i < sectionCount; i++)
            {
                int desc = 128 + i * 72;            // 72-byte section descriptor
                if (desc + 72 > b.Length) break;
                var g = new Guid(b.AsSpan(desc + 0x10, 16)); // SectionType GUID at descriptor offset 0x10
                if (g == SecMemory || g == SecMemory2) mem = true;
                else if (g == SecProcGen || g == SecProcX64) proc = true;
                else if (g == SecPcie || g == SecPciBus || g == SecPciDev) pcie = true;
            }
            // Memory is the most specific attribution we care about, then processor, then PCIe.
            if (mem) return WheaDomain.MemoryImc;
            if (proc) return WheaDomain.CpuCore;
            if (pcie) return WheaDomain.PciePlatform;
        }
        catch { /* malformed blob -> let the caller fall back to the event ID */ }
        return WheaDomain.Unknown;
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

    /// <summary>Decode a hex-text field (e.g. the CPER "RawData" blob) into bytes.</summary>
    private static byte[]? GetBytes(Dictionary<string, string> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) return null;
        var hex = new System.Text.StringBuilder(v.Length);
        foreach (char c in v)
            if (Uri.IsHexDigit(c)) hex.Append(c);
        if (hex.Length < 2) return null;
        int n = hex.Length / 2;
        var bytes = new byte[n];
        try
        {
            for (int i = 0; i < n; i++)
                bytes[i] = (byte)((Uri.FromHex(hex[i * 2]) << 4) | Uri.FromHex(hex[i * 2 + 1]));
        }
        catch { return null; }
        return bytes;
    }
}
