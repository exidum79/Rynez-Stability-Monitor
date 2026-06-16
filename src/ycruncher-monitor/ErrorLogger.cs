using System.Globalization;
using System.Text;

namespace Rynez;

/// <summary>
/// Writes every event (error/observation) permanently to a CSV file.
/// Key point: the Windows Event Log rotates and gets purged, but this file stays.
/// -> directly fixes the "per-core errors never get recorded" problem.
/// Multiple threads (workers + WHEA listener + watchdog) write concurrently, so it is locked.
/// </summary>
public sealed class ErrorLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    public string FilePath { get; }

    // Per-core running counts (scoreboard). Key = physical core index.
    private readonly Dictionary<int, CoreErrorTally> _tally = new();

    /// <summary>Fired (outside the lock) whenever a real instability signature is recorded:
    /// (source, coreIndex, detail). Used to stop the run as soon as a problem core is found.</summary>
    public Action<string, int, string>? OnInstability;

    public ErrorLogger(string logDir)
    {
        Directory.CreateDirectory(logDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        FilePath = Path.Combine(logDir, $"rynez_{stamp}.csv");

        _writer = new StreamWriter(FilePath, append: true, Encoding.UTF8) { AutoFlush = true };
        _writer.WriteLine("timestamp,source,coreUnderTest,apicId,eventId,clockMHz,voltageV,tempC,detail");
    }

    /// <summary>Write one row. source e.g.: WHEA / SELFCHECK / TRANSIENT / HANG / CYCLE / INFO.</summary>
    public void Log(string source, int coreUnderTest, string apicId, string eventId, string detail)
    {
        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string telemFields = ",,"; // sensor/telemetry feature dropped -> clock/voltage/temp columns kept empty
        string line = string.Join(",",
            ts, source, coreUnderTest.ToString(), Csv(apicId), Csv(eventId), telemFields, Csv(detail));

        bool isInstability = source is "SELFCHECK" or "TRANSIENT" or "HANG";
        lock (_gate)
        {
            _writer.WriteLine(line);

            // Scoreboard counts only real instability signatures.
            if (isInstability)
            {
                if (!_tally.TryGetValue(coreUnderTest, out var t))
                    _tally[coreUnderTest] = t = new CoreErrorTally();
                switch (source)
                {
                    case "SELFCHECK": t.SelfCheck++; break;
                    case "TRANSIENT": t.Transient++; break;
                    case "HANG": t.Hang++; break;
                }
            }
        }

        // Notify outside the lock (callback may cancel the run).
        if (isInstability)
            OnInstability?.Invoke(source, coreUnderTest, detail);
    }

    /// <summary>Print the per-core instability scoreboard.</summary>
    public void PrintScoreboard()
    {
        lock (_gate)
        {
            Console.WriteLine();
            Console.WriteLine("-------- Per-core instability scoreboard --------");
            if (_tally.Count == 0)
            {
                Console.WriteLine("  (no errors - stable so far)");
            }
            else
            {
                foreach (var kv in _tally.OrderByDescending(k => k.Value.Total))
                    Console.WriteLine($"  core {kv.Key,2}: silent-error {kv.Value.SelfCheck}, transient {kv.Value.Transient}, hang {kv.Value.Hang}  (total {kv.Value.Total})");
            }
            Console.WriteLine($"  log file: {FilePath}");
            Console.WriteLine("-------------------------------------------------");
        }
    }

    public bool HasAnyError
    {
        get { lock (_gate) { return _tally.Count > 0; } }
    }

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    public void Dispose()
    {
        lock (_gate) { _writer.Flush(); _writer.Dispose(); }
    }

    private sealed class CoreErrorTally
    {
        public int SelfCheck, Transient, Hang;
        public int Total => SelfCheck + Transient + Hang;
    }
}
