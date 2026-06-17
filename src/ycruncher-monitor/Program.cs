using Rynez;
using System.Diagnostics;

// ========================================================================
//  Rynez Stability Monitor
//  AMD Ryzen Curve Optimizer diagnosis. Drives y-cruncher stress in two modes:
//    - all-core  : every core loaded (load Vdroop / heat regime)
//    - single    : one core pinned at a time (high single-core boost regime - where CO crashes hide)
//  Wraps it with a micro-freeze monitor, a reboot-surviving crash breadcrumb, per-run timing +
//  slowdown detection, stop-on-detection, and a permanent CSV log - so the failing core is recorded
//  even when Windows logs nothing. (Manual tool: it reports the bad core; you adjust the Curve
//  Optimizer yourself. It does NOT auto-tune.)
// ========================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;
Native.timeBeginPeriod(1); // 1ms timer resolution so the micro-freeze monitor timing is precise
Console.WriteLine("==================================================");
Console.WriteLine("   Rynez Stability Monitor");
Console.WriteLine("   AMD Ryzen CO diagnosis - y-cruncher all-core / single-core");
Console.WriteLine("==================================================");
Console.WriteLine();

// ---- Options ----
//   --single       : single-core mode (pin one core at a time = high-boost CO testing). Default: all-core.
//   --seconds N    : seconds per individual test (default 120; internally capped to 60s/test).
//                    One run = a full pass of every test, so total run time scales with test count.
//   --cycles  N    : passes (all-core: # of runs; single: # of full sweeps over every core). 0 = infinite.
//   --stop-on N    : stop after N problem events (default 1 = stop on first). 0 = never stop.
//   --yc-tests "BKT FFTv4 N63 VT3" : y-cruncher tests tuned for PBO Curve Optimizer hunting:
//        BKT (lightest -> highest boost, exposes too-aggressive CO at high freq),
//        FFTv4 (heaviest AVX-512 -> max current/heat, load Vdroop CO),
//        N63 (NTT integer path -> silent errors FFT misses), VT3 (memory-coupled).
//        Valid tokens: BKT BBP SFTv4 SNT SVT FFTv4 NTT63 N63 VSTv3 VT3. --yc-mem 1.2G
//   --no-hitch [--hitch-ms 15] : micro-freeze monitor is ON by default; --no-hitch turns it off.
//   --core N       : single-core mode on ONLY physical core N (continuous soak of one suspect core).
//   --cores 0,2,5  : single-core mode on ONLY the listed physical cores (comma-separated).
//                    Both imply --single. With neither, single mode sweeps every core.
bool single = args.Any(a => a.Equals("--single", StringComparison.OrdinalIgnoreCase));
int perCoreSeconds = GetIntArg(args, "--seconds", 120);
int cycles = GetIntArg(args, "--cycles", 0);
int stopOn = GetIntArg(args, "--stop-on", 1);
string ycTests = GetStrArg(args, "--yc-tests", "BKT FFTv4 N63 VT3");
string ycMem = GetStrArg(args, "--yc-mem", ""); // empty = let y-cruncher auto-size (good for all-core)
var coreSel = ParseCoreSelection(args);          // requested physical core indices (empty = all)
if (coreSel.Count > 0) single = true;            // selecting cores only makes sense pinned -> force single-core

string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
using var logger = new ErrorLogger(logDir);
using var trail = new CrashTrail(logDir);
using var job = new JobObject(); // kills y-cruncher if this monitor window is closed (X) or otherwise dies
string? prevAlive = trail.ReadPrevious();

// ---- [1/3] CPU ----
Console.WriteLine("[1/3] Detecting CPU...");
var topo = new CpuTopology();
topo.PrintSummary();
Console.WriteLine("  Core numbering (0-based, OS order - cross-check with Ryzen Master / BIOS):");
foreach (var c in topo.Cores)
{
    var logs = Enumerable.Range(0, 64).Where(b => (c.Mask & (1UL << b)) != 0);
    Console.WriteLine($"    physical core {c.Index} = logical {string.Join(",", logs)}");
}
logger.Log("INFO", -1, "", "", $"Detected: {topo.Cores.Count} physical cores / {topo.LogicalCount} logical / SMT {(topo.SmtEnabled ? "ON" : "OFF")}");
Console.WriteLine();

var currentCore = new CurrentCoreHolder();

// ---- [2/3] Admin + previous-crash breadcrumb ----
Console.WriteLine($"[2/3] Administrator: {(Admin.IsElevated() ? "yes" : "no")}");
if (prevAlive != null && !prevAlive.Contains("CLEAN_EXIT"))
{
    Console.WriteLine("  [!] Previous run did NOT exit cleanly - likely a hard reboot/freeze. Last breadcrumb:");
    foreach (var ln in prevAlive.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        Console.WriteLine($"        {ln.Trim()}");
    Console.WriteLine("      -> That core was running when the machine died = prime suspect.");
    logger.Log("PREV-CRASH", -1, "", "", "Previous run did not exit cleanly. Last breadcrumb: " + prevAlive.Replace('\n', ' ').Trim());
}
else if (prevAlive != null)
    Console.WriteLine("  Previous run exited cleanly.");
Console.WriteLine();

// ---- [3/3] Engine: y-cruncher (required) ----
Console.WriteLine("[3/3] Engine: y-cruncher");
if (!YCruncherRunner.Exists())
{
    Console.WriteLine($"  y-cruncher.exe not found at {YCruncherRunner.DefaultExePath()}");
    Console.WriteLine("  Download from http://www.numberworld.org/y-cruncher/ and put y-cruncher.exe in the tools\\ folder.");
    Native.timeEndPeriod(1);
    return;
}
var ycRunner = new YCruncherRunner(logger, ycTests, ycMem, topo.CoreOfLogical, job);
string memDesc = string.IsNullOrWhiteSpace(ycMem) ? "auto" : ycMem;

// Resolve which physical cores single-core mode will test (default: all of them).
var coresToTest = topo.Cores.ToList();
if (single && coreSel.Count > 0)
{
    var invalid = coreSel.Where(i => i < 0 || i >= topo.Cores.Count).Distinct().ToList();
    if (invalid.Count > 0)
        Console.WriteLine($"  [!] ignoring out-of-range core(s): {string.Join(",", invalid)} (valid range 0..{topo.Cores.Count - 1})");
    var valid = coreSel.Where(i => i >= 0 && i < topo.Cores.Count).Distinct().ToHashSet();
    if (valid.Count == 0)
    {
        Console.WriteLine("  [!] no valid cores selected (--core/--cores) - nothing to test. Exiting.");
        Native.timeEndPeriod(1);
        return;
    }
    coresToTest = topo.Cores.Where(c => valid.Contains(c.Index)).ToList();
}

if (single)
{
    bool allCores = coresToTest.Count == topo.Cores.Count;
    string coreList = allCores ? "every core" : $"core(s) {string.Join(",", coresToTest.Select(c => c.Index))}";
    Console.WriteLine($"  SINGLE-CORE mode: one core pinned at a time (high single-core boost), tests: {ycTests}, mem: {memDesc}");
    Console.WriteLine($"  testing {coreList}; per core one full pass of all tests, {(cycles == 0 ? "looping until you stop / first error" : cycles + " sweep(s)")}");
    Console.WriteLine("  Any error/micro-freeze on a pinned core is blamed on THAT core.");
}
else
{
    Console.WriteLine($"  ALL-CORE mode: y-cruncher self-pins to every core, tests: {ycTests}, mem: {memDesc}");
    Console.WriteLine($"  per run: one full pass of all tests, {(cycles == 0 ? "looping until you stop / first error" : cycles + " run(s)")}");
    Console.WriteLine("  On a y-cruncher error, the failing core (from its own message) is reported.");
}

// ---- Monitors ----
using var cts = new CancellationTokenSource();
using var shutdownDone = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; Console.WriteLine("\nStop requested - cleaning up..."); cts.Cancel(); };

// Closing the window (X button), logoff or shutdown: stop y-cruncher and let cleanup run before the OS
// terminates us. We block in the handler until the finally block signals it is done (or ~4s elapse).
// (The kill-on-close job object is the hard backstop if this handler never gets to run.)
Native.ConsoleCtrlHandler closeHandler = ctrlType =>
{
    if (ctrlType is Native.CTRL_CLOSE_EVENT or Native.CTRL_LOGOFF_EVENT or Native.CTRL_SHUTDOWN_EVENT)
    {
        Console.WriteLine("\nWindow closing - stopping y-cruncher...");
        cts.Cancel();
        shutdownDone.Wait(4000);
        return true;
    }
    return false; // Ctrl+C etc. -> let .NET's CancelKeyPress handle it
};
Native.SetConsoleCtrlHandler(closeHandler, true);

// Micro-freeze monitor is ON by default (it doubles as the sensor-free performance-degradation signal:
// a momentary stall = the core/system briefly couldn't keep up). --no-hitch disables it.
StallMonitor? hitch = null;
if (!args.Any(a => a.Equals("--no-hitch", StringComparison.OrdinalIgnoreCase)))
{
    double hitchMs = GetIntArg(args, "--hitch-ms", 15);
    hitch = new StallMonitor(logger, hitchMs, cts.Token, () => currentCore.Value);
    hitch.Start();
    Console.WriteLine($"  micro-freeze monitor ON (>= {hitchMs:F0}ms). single-core: blamed on the pinned core; all-core: informational.");
}
Console.WriteLine();

// ---- Stop as soon as a problem core is found, and remember it ----
int instCount = 0, culpritCore = -1;
string culpritSig = "", culpritDetail = "";
logger.OnInstability = (src, core, detail) =>
{
    if (Interlocked.CompareExchange(ref culpritCore, core, -1) == -1) { culpritSig = src; culpritDetail = detail; }
    int n = Interlocked.Increment(ref instCount);
    if (stopOn > 0 && n >= stopOn && !cts.IsCancellationRequested)
    {
        Console.WriteLine($"\n*** PROBLEM FOUND on {(core >= 0 ? $"core {core}" : "UNKNOWN core")} [{src}] -> stopping. ***");
        cts.Cancel();
    }
};

// ---- 1-second crash breadcrumb (survives a hard reboot, names the core under test) ----
int cycle = 0;
trail.Start(() => $"engine=y-cruncher\nmode={(single ? "single" : "allcore")}\npass={cycle}\ncore_under_test={(currentCore.Value < 0 ? "-" : currentCore.Value.ToString())}",
            cts.Token);

// ---- Timing + sensor-free slowdown detection ----
var totalSw = Stopwatch.StartNew();
var runDurations = new List<double>(); // wall-seconds of COMPLETED runs -> baseline for slowdown
int runNo = 0;

string FmtElapsed()
{
    var t = totalSw.Elapsed;
    return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";
}

void HandleResult(YcResult r, int pinnedCore)
{
    // Single-core: WE pinned the core, so that pinned core is the authority. y-cruncher's own
    // "logical core N" message can use a relative/internal index when affinity-confined, so we only
    // fall back to it in all-core mode (pinnedCore < 0).
    int blamed = pinnedCore >= 0 ? pinnedCore : r.Core;
    switch (r.Outcome)
    {
        case YcOutcome.ErrorDetected:
            logger.Log("SELFCHECK", blamed, "", "y-cruncher", $"y-cruncher error: {r.Detail}");
            Console.WriteLine($"    [!] y-cruncher ERROR{(blamed >= 0 ? $" on core {blamed}" : "")}:");
            if (pinnedCore >= 0 && r.Core >= 0 && r.Core != pinnedCore)
                Console.WriteLine($"        (note: this run was pinned to core {pinnedCore}, but y-cruncher's message maps to physical core {r.Core}." +
                                  " If Task Manager shows MORE than one core loaded in single-core mode, the affinity confinement isn't working yet - treat the core number with caution.)");
            foreach (var dl in r.Detail.Split('\n')) Console.WriteLine("        " + dl);
            break;
        case YcOutcome.Crashed:
            // Engine-level failure (didn't run / crashed) - NOT core instability. Stop instead of racing.
            logger.Log("ENGINE", pinnedCore, "", "y-cruncher", $"y-cruncher engine problem: {r.Detail}");
            Console.WriteLine($"    [!] y-cruncher did not run / crashed: {r.Detail}");
            Console.WriteLine("    -> stopping (fix the above, then re-run).");
            cts.Cancel();
            break;
        case YcOutcome.Hung:
            logger.Log("HANG", pinnedCore, "", "y-cruncher", $"y-cruncher hang: {r.Detail}");
            Console.WriteLine($"    [STOP] machine stalled during y-cruncher: {r.Detail}");
            break;
        case YcOutcome.Completed:
            Console.WriteLine($"    passed (no error this run).");
            break;
    }
}

// Sensor-free performance-degradation check: a COMPLETED run is bounded by y-cruncher's -TL, so its
// wall-time should be near-constant. If it runs noticeably LONGER than the running average, the machine
// spent time NOT executing (stalls / throttling / clock stretching) = a slowdown worth recording.
void CheckSlowdown(double durationSec, YcResult r, int pinnedCore)
{
    if (r.Outcome != YcOutcome.Completed) return;
    if (runDurations.Count >= 3)
    {
        double avg = runDurations.Average();
        if (durationSec > avg * 1.25 && durationSec - avg > 5)
        {
            double pct = (durationSec / avg - 1) * 100;
            string where = pinnedCore >= 0 ? $"core {pinnedCore}" : "all-core";
            logger.Log("SLOWDOWN", pinnedCore, "", "perf", $"run {durationSec:F0}s vs avg {avg:F0}s (+{pct:F0}%) on {where} -> possible clock stretching / throttling");
            Console.WriteLine($"    [SLOWDOWN] this run {durationSec:F0}s vs avg {avg:F0}s (+{pct:F0}%) -> possible performance drop ({where})");
        }
    }
    runDurations.Add(durationSec);
}

Console.WriteLine($"Starting (Ctrl+C to stop)...");
Console.WriteLine();
try
{
    while (!cts.IsCancellationRequested && (cycles == 0 || cycle < cycles))
    {
        cycle++;
        if (single)
        {
            foreach (var core in coresToTest)
            {
                if (cts.IsCancellationRequested) break;
                runNo++;
                currentCore.Value = core.Index;
                Console.WriteLine($"-- run {runNo} | sweep {cycle}{(cycles == 0 ? "" : "/" + cycles)} | core {core.Index} | elapsed {FmtElapsed()} --");
                logger.Log("CYCLE", core.Index, "", "", $"sweep {cycle} core {core.Index} start (single-core)");
                Console.WriteLine($"  > y-cruncher single-core stress, pinned to core {core.Index} (one full pass of all tests)...");

                var sw = Stopwatch.StartNew();
                var r = ycRunner.RunSingleCore(core.Mask, perCoreSeconds, cts.Token);
                sw.Stop();
                HandleResult(r, core.Index);
                Console.WriteLine($"    this run {sw.Elapsed.TotalSeconds:F0}s | elapsed {FmtElapsed()}");
                CheckSlowdown(sw.Elapsed.TotalSeconds, r, core.Index);
                logger.PrintScoreboard();
            }
            currentCore.Value = -1;
        }
        else
        {
            runNo++;
            Console.WriteLine($"-- run {runNo}{(cycles == 0 ? "" : "/" + cycles)} | elapsed {FmtElapsed()} --");
            logger.Log("CYCLE", -1, "", "", $"run {runNo} start (all-core)");
            Console.WriteLine($"  > y-cruncher all-core stress (one full pass of all tests)...");

            var sw = Stopwatch.StartNew();
            var r = ycRunner.RunAllCore(perCoreSeconds, cts.Token);
            sw.Stop();
            HandleResult(r, -1);
            Console.WriteLine($"    this run {sw.Elapsed.TotalSeconds:F0}s | elapsed {FmtElapsed()}");
            CheckSlowdown(sw.Elapsed.TotalSeconds, r, -1);
            logger.PrintScoreboard();
        }
    }
}
finally
{
    currentCore.Value = -1;
    cts.Cancel();
    trail.MarkCleanExit();
    Console.WriteLine();
    Console.WriteLine($"Total test time: {FmtElapsed()} ({runNo} run(s)).");
    if (instCount > 0)
    {
        string who = culpritCore >= 0 ? $"core {culpritCore}" : "UNKNOWN core (y-cruncher did not name one)";
        Console.WriteLine("############################################################");
        Console.WriteLine($"#  PROBLEM FOUND: {who}");
        Console.WriteLine($"#  signature: {culpritSig}");
        if (culpritCore >= 0)
        {
            Console.WriteLine("#  -> This core showed an error/instability during its test.");
            Console.WriteLine("#     (What to change is your call.)");
        }
        else
        {
            Console.WriteLine("#  -> An error occurred but the core could not be parsed.");
            Console.WriteLine("#     See the y-cruncher output below for the failing logical core.");
        }
        Console.WriteLine("############################################################");
        Console.WriteLine("y-cruncher said:");
        foreach (var dl in culpritDetail.Split('\n')) Console.WriteLine("  " + dl);
        Console.WriteLine();
    }
    Console.WriteLine("======== Final result ========");
    logger.PrintScoreboard();
    if (hitch != null) { Console.WriteLine($"Micro-freezes: {hitch.HitchCount} (worst {hitch.WorstMs:F0}ms)."); hitch.Dispose(); }
    if (instCount == 0)
        Console.WriteLine("-> No problem found. Run longer (--cycles/--seconds), try --single for high-boost CO, or stronger --yc-tests.");
    Native.timeEndPeriod(1);
    shutdownDone.Set(); // release the window-close handler if it is waiting on us
}
GC.KeepAlive(closeHandler); // keep the native callback alive for the whole process lifetime

// ---- helpers ----
static int GetIntArg(string[] args, string name, int fallback)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int v)) return v;
    return fallback;
}
static string GetStrArg(string[] args, string name, string fallback)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
    return fallback;
}
// Collect requested physical-core indices from --cores 0,2,5 and/or --core N. Empty = test all cores.
static List<int> ParseCoreSelection(string[] args)
{
    var list = new List<int>();
    foreach (var tok in GetStrArg(args, "--cores", "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        if (int.TryParse(tok.Trim(), out int v)) list.Add(v);
    int one = GetIntArg(args, "--core", -1);
    if (one >= 0) list.Add(one);
    return list;
}

sealed class CurrentCoreHolder
{
    private volatile int _value = -1;
    public int Value { get => _value; set => _value = value; }
}
