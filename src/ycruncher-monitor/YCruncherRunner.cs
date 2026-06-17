using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Rynez;

public enum YcOutcome { Completed, ErrorDetected, Crashed, Hung, Cancelled, NotFound }

public readonly record struct YcResult(YcOutcome Outcome, string Detail, int Core);

/// <summary>
/// Runs y-cruncher's stress test as the load engine (heavy AVX-512 + large memory + its own error
/// checking). Two modes:
///   - All-core: y-cruncher self-pins across every core; we read WHICH logical core it blames in its
///     error message ("Error(s) encountered on logical core N") -> map to a physical core.
///   - Single-core: single-threaded (-PF:none) + process affinity pinned to ONE physical core, so that
///     core boosts to its single-core ceiling and Curve Optimizer instability shows in the high-boost
///     regime all-core testing misses; the core under test is known (we pinned it), so the caller
///     attributes any error/hitch to it.
///
/// Outcomes:
///   ErrorDetected (+ Core)  - y-cruncher caught a wrong computation; it names the failing core
///   Hung                    - the log stopped growing (whole machine stalled)
///   Crashed                 - process exited early / nonzero
/// </summary>
public sealed class YCruncherRunner
{
    private readonly ErrorLogger _logger;
    private readonly string _tests;
    private readonly string _mem;
    private readonly Func<int, int> _coreOfLogical; // OS logical processor -> physical core index (exact)
    private readonly JobObject? _job;               // kills y-cruncher if the monitor process goes away

    public YCruncherRunner(ErrorLogger logger, string tests, string mem, Func<int, int> coreOfLogical, JobObject? job = null)
    {
        _logger = logger; _tests = tests; _mem = mem;
        _coreOfLogical = coreOfLogical;
        _job = job;
    }

    public static string DefaultExePath() => Path.Combine(AppContext.BaseDirectory, "tools", "y-cruncher.exe");
    public static bool Exists() => File.Exists(DefaultExePath());

    /// <summary>Path to y-cruncher's Binaries folder (the per-architecture worker .exe files).</summary>
    public static string BinariesDir() => Path.Combine(AppContext.BaseDirectory, "tools", "Binaries");

    /// <summary>
    /// True if the per-architecture worker binaries are present. y-cruncher.exe is only a launcher; the
    /// real stress runs in a worker from tools\Binaries\ (e.g. "24-ZN5 ~ Komari.exe"). If a user extracted
    /// ONLY y-cruncher.exe, this is false and the tool cannot actually stress-test.
    /// </summary>
    public static bool BinariesPresent()
    {
        try { return Directory.Exists(BinariesDir()) && Directory.EnumerateFiles(BinariesDir(), "*.exe").Any(); }
        catch { return false; }
    }

    /// <summary>Run y-cruncher stress on ALL cores for up to durationSeconds (full pass of all tests).</summary>
    public YcResult RunAllCore(int durationSeconds, CancellationToken token)
        => Run(durationSeconds, affinityMask: 0UL, token);

    /// <summary>
    /// Run y-cruncher stress pinned to ONE physical core (its logical-processor <paramref name="affinityMask"/>).
    /// Only that core is loaded, so it boosts to its single-core ceiling - exposing Curve Optimizer
    /// instability in the high-boost regime that all-core (clock-suppressed) testing misses. Confinement
    /// is by process affinity alone (y-cruncher's stress command has no thread/core option).
    /// </summary>
    public YcResult RunSingleCore(ulong affinityMask, int durationSeconds, CancellationToken token)
        => Run(durationSeconds, affinityMask, token);

    /// <summary>
    /// Single-core TRANSIENT / boost-cycling mode: pin y-cruncher to ONE physical core (so it boosts to
    /// its single-core ceiling) AND duty-cycle its worker - run for <paramref name="burstMs"/>, suspend it
    /// for <paramref name="idleMs"/>, repeat - so the core keeps ramping idle->load. That rapid boost-clock
    /// swing is where a class of Curve Optimizer faults hides that a STEADY 100% load never triggers.
    /// y-cruncher's own math self-check still runs, so a silent compute error is still caught.
    /// NOTE: Windows timer granularity is ~0.5-2ms, NOT sub-ms - this approximates transient stress; it
    /// does not match a Linux sub-ms tool. It surfaces more transient exposure than steady load, no more.
    /// </summary>
    public YcResult RunTransient(ulong affinityMask, int durationSeconds, int burstMs, int idleMs, CancellationToken token)
        => Run(durationSeconds, affinityMask, token, Math.Max(1, burstMs), Math.Max(1, idleMs));

    private YcResult Run(int durationSeconds, ulong affinityMask, CancellationToken token, int burstMs = 0, int idleMs = 0)
    {
        string exe = DefaultExePath();
        if (!File.Exists(exe)) return new(YcOutcome.NotFound, "y-cruncher.exe not found in tools\\", -1);

        string dir = Path.GetDirectoryName(exe)!;
        string logPath = Path.Combine(dir, "yc_stress.log");
        try { File.Delete(logPath); } catch { }

        string memArg = string.IsNullOrWhiteSpace(_mem) ? "" : $"-M:{_mem} ";
        // -D = per-test duration (rotate algorithms). -TL bounds one run to a FULL pass of every
        // test (perTest x testCount); a fixed/short -TL would cut the run off before the last test
        // in the list (e.g. VT3) ever starts, so the limit must scale with the number of tests.
        int perTest = Math.Min(60, Math.Max(10, durationSeconds));
        int testCount = Math.Max(1, _tests.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        int totalLimit = perTest * testCount;
        // y-cruncher's `stress` command rejects engine options like -TD/-PF ("Invalid Parameter" -> it
        // exits immediately without running), so we must NOT pass them. Single-core confinement is done
        // purely by the process affinity set after launch.
        string args = $"logfile:\"{logPath}\" pause:-2 stress {memArg}-D:{perTest} -TL:{totalLimit} {_tests}";

        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args, WorkingDirectory = dir,
            UseShellExecute = false, CreateNoWindow = true,
        };

        // Single-core: a per-run job with a HARD affinity limit (y-cruncher self-pins to all cores, but a
        // job affinity cannot be escaped) plus kill-on-close. All-core: the shared kill-on-close job. The
        // launcher must be assigned to the job BEFORE it spawns its arch-binary child, so the child (which
        // does the real stress) inherits the job and stays confined too.
        JobObject? affinityJob = affinityMask != 0UL ? new JobObject(affinityMask) : null;

        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex) { affinityJob?.Dispose(); return new(YcOutcome.Crashed, $"failed to launch y-cruncher: {ex.Message}", -1); }

        // Assign ASAP (before the child spawns). The job affinity is the real single-core boundary; the
        // process-affinity call below is just a belt-and-suspenders hint.
        (affinityJob ?? _job)?.Assign(proc);
        if (affinityMask != 0UL)
        {
            try { proc.ProcessorAffinity = (IntPtr)(long)affinityMask; } catch { }
        }

        // Transient / boost-cycling: a background thread duty-cycles the pinned worker (suspend/resume) so
        // the core ramps idle->load repeatedly instead of holding a steady clock. Only when burstMs > 0.
        Thread? dutyThread = null;
        if (burstMs > 0)
        {
            dutyThread = new Thread(() => DutyCycleWorker(proc, burstMs, idleMs, token))
            { IsBackground = true, Priority = ThreadPriority.Highest, Name = "transient-duty" };
            dutyThread.Start();
        }

        var runSw = Stopwatch.StartNew();
        try
        {
            // No internal hang detection: y-cruncher.exe is only a launcher (the real work is in a child
            // process), so neither its CPU time nor the log file grow reliably. y-cruncher's own -TL time
            // limit bounds the run; a real machine freeze is caught by the crash breadcrumb after reboot.
            // We just scan the log for a real error and watch for an early/abnormal exit.
            double lastBeat = 0;
            while (!proc.HasExited)
            {
                if (token.IsCancellationRequested) { TryKill(proc); return new(YcOutcome.Cancelled, "", -1); }
                var (_, errLine) = ScanLog(logPath);
                if (errLine != null) { TryKill(proc); return Error(errLine, logPath); }
                // Heartbeat so a multi-minute run doesn't look frozen (the console is otherwise silent
                // until error/completion, and y-cruncher.exe itself sits at ~0% - its child does the work).
                double el = runSw.Elapsed.TotalSeconds;
                if (el - lastBeat >= 15)
                {
                    lastBeat = el;
                    string? prog = LatestProgress(logPath);
                    Console.WriteLine($"      ... running {el:F0}s / ~{totalLimit}s{(prog != null ? " | " + prog : "")}");
                }
                proc.WaitForExit(500);
            }

            var (_, finalErr) = ScanLog(logPath);
            if (finalErr != null) return Error(finalErr, logPath);
            if (proc.ExitCode != 0) return new(YcOutcome.Crashed, $"y-cruncher exited with code {proc.ExitCode}", -1);
            // A real run lasts ~totalLimit seconds. An almost-instant clean exit means y-cruncher never
            // actually stressed (bad argument, missing arch binary, ...) - surface it as a crash instead of
            // silently counting it as "passed", which would race through thousands of fake 0-second runs.
            if (runSw.Elapsed.TotalSeconds < Math.Max(5, totalLimit * 0.3))
                return new(YcOutcome.Crashed, $"exited after only {runSw.Elapsed.TotalSeconds:F0}s (expected ~{totalLimit}s) - it did not actually run. See tools\\yc_stress.log (e.g. invalid parameter / missing binary).", -1);
            return new(YcOutcome.Completed, "", -1);
        }
        finally { try { dutyThread?.Join(1500); } catch { } proc.Dispose(); affinityJob?.Dispose(); }
    }

    // Find the y-cruncher worker child (the busy process in tools\Binaries\) and duty-cycle it: let it run
    // for burstMs (core ramps to boost), suspend it for idleMs (core drops clock), repeat. Pinned to one
    // core, this produces the rapid boost swings a steady 100% load never does. y-cruncher's self-check is
    // untouched - suspend/resume is transparent to it - so a silent compute error is still detected.
    private void DutyCycleWorker(Process launcher, int burstMs, int idleMs, CancellationToken token)
    {
        Process? worker = FindWorkerChild(token);
        if (worker == null)
        {
            Console.WriteLine("      [transient] could not find the y-cruncher worker child - this run falls back to steady single-core load.");
            return;
        }
        IntPtr h = Native.OpenForSuspendResume(worker.Id);
        if (h == IntPtr.Zero)
        {
            Console.WriteLine("      [transient] could not open the worker for suspend/resume - this run falls back to steady single-core load.");
            worker.Dispose();
            return;
        }
        try
        {
            while (!token.IsCancellationRequested && !SafeHasExited(launcher) && !SafeHasExited(worker))
            {
                Thread.Sleep(burstMs);                          // load burst: worker runs, core ramps up
                if (token.IsCancellationRequested || SafeHasExited(worker)) break;
                Native.SuspendProcess(h);                       // idle gap: core drops its clock
                Thread.Sleep(idleMs);
                Native.ResumeProcess(h);                        // next load burst -> boost ramp again
            }
        }
        finally
        {
            Native.ResumeProcess(h);                            // never leave the worker frozen
            Native.CloseProcessHandle(h);
            worker.Dispose();
        }
    }

    private static bool SafeHasExited(Process p) { try { return p.HasExited; } catch { return true; } }

    // The worker is the running process whose image lives in tools\Binaries\ (e.g. "24-ZN5 ~ Komari.exe").
    // y-cruncher.exe is only the launcher; in this tool's setup the worker is the only process running from
    // that folder. Polls briefly because the launcher spawns the worker a moment after start.
    private Process? FindWorkerChild(CancellationToken token)
    {
        string binDir = BinariesDir().TrimEnd('\\', '/');
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 10 && !token.IsCancellationRequested)
        {
            foreach (var p in Process.GetProcesses())
            {
                bool match = false;
                try { match = p.MainModule?.FileName?.StartsWith(binDir, StringComparison.OrdinalIgnoreCase) == true; }
                catch { /* access denied / exited -> not our worker */ }
                if (match) return p;
                try { p.Dispose(); } catch { }
            }
            Thread.Sleep(150);
        }
        return null;
    }

    private YcResult Error(string line, string logPath)
    {
        // Capture the y-cruncher error block (log tail) so the user does NOT have to open the log file.
        string tail = ReadTail(logPath, 25);
        string detail = string.IsNullOrEmpty(tail) ? line : line + "\n--- y-cruncher output (tail) ---\n" + tail;

        // The failing core ("Error(s) encountered on logical core N") may be on a DIFFERENT line than the
        // line that first matched (e.g. "Checksum Mismatch"), so search the WHOLE block, last match wins.
        int core = -1;
        foreach (Match m in Regex.Matches(detail, @"logical core\s+(\d+)", RegexOptions.IgnoreCase))
            if (int.TryParse(m.Groups[1].Value, out int logical))
                core = _coreOfLogical(logical);

        return new(YcOutcome.ErrorDetected, detail, core);
    }

    // Pull the most recent progress line from y-cruncher's log (proof the worker child is alive).
    private static string? LatestProgress(string path)
    {
        try
        {
            string tail = ReadTail(path, 12);
            string? best = null;
            foreach (var raw in tail.Split('\n'))
            {
                string l = raw.Trim();
                if (l.Contains("Elapsed Time", StringComparison.OrdinalIgnoreCase)
                    || l.StartsWith("Testing", StringComparison.OrdinalIgnoreCase)
                    || l.StartsWith("Iteration", StringComparison.OrdinalIgnoreCase))
                    best = l;
            }
            return best;
        }
        catch { return null; }
    }

    private static string ReadTail(string path, int lines)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var all = new List<string>();
            string? l;
            while ((l = sr.ReadLine()) != null)
                if (l.Trim().Length > 0) all.Add(l.TrimEnd());
            int take = Math.Min(lines, all.Count);
            return string.Join("\n", all.GetRange(all.Count - take, take));
        }
        catch { return ""; }
    }

    private static (long len, string? error) ScanLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return (0, null);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long len = fs.Length;
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) != null)
                if (LooksLikeError(line)) return (len, line.Trim());
            return (len, null);
        }
        catch { return (0, null); }
    }

    // "Stop on Error: Enabled" in the config banner is NOT an error. Skip benign lines.
    private static bool LooksLikeError(string line)
    {
        string l = line.ToLowerInvariant();
        if (l.Contains("stop on error") || l.Contains("stop-on-error") || l.Contains("error checking")
            || l.Contains("errors: 0") || l.Contains("0 errors") || l.Contains("no error"))
            return false;
        return l.Contains("error") || l.Contains("mismatch") || l.Contains("incorrect")
            || l.Contains("miscompare") || l.Contains("hardware error") || l.Contains("computation error");
    }

    private static void TryKill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch { } }
}
