using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Rynez;

/// <summary>
/// Random-mode performance-degradation probe (sensor-free silent-slowdown detection for --random).
///
/// WHY this exists: the per-run wall-time slowdown check (Program.CheckSlowdown) is disabled in --random
/// mode because the worker is duty-cycled for a RANDOM fraction of each run, so wall-time varies by design.
/// But silent slowdown - a marginal Curve Optimizer core that CLOCK-STRETCHES (the CPU's defence: it
/// inserts stall cycles to keep timing margin under voltage droop) - is exactly the failure that shows up
/// in real gaming as stutter / FPS loss WITHOUT a crash. So we measure it directly instead.
///
/// HOW: periodically the duty thread suspends the y-cruncher worker (freeing the pinned core), then runs a
/// FIXED-work AVX2-FMA kernel - the SAME instruction count every time - pinned to the core under test, and
/// times it. Fixed work means the time depends only on the core's effective throughput (frequency x IPC).
/// If the core is clock-stretching or throttling, the same work takes measurably longer. We compare each
/// probe's BEST-of-K timing (min rejects scheduler/interrupt noise, which only ever slows a sample) against
/// a baseline locked from the first several probes; a sustained slowdown is logged.
///
/// HONEST LIMITS:
///  - It cannot distinguish CO clock-stretching from ordinary THERMAL throttling (no temperature sensor).
///    A flagged slowdown means "this core silently lost performance" - could be instability OR just heat.
///  - The probe runs in ISOLATION (worker suspended) so its di/dt is its own AVX2 load, lighter than
///    y-cruncher's full AVX-512 - it may under-trigger a stretch that only the heavier real load causes.
///    (Actual wrong-result errors under the real load are still caught by y-cruncher's own self-check.)
/// </summary>
public sealed class PerfProbe
{
    // Fixed work per kernel call. Absolute value is irrelevant (the baseline is self-relative); it only
    // needs to be big enough that one call lasts a few ms (well above the ~100ns Stopwatch resolution) and
    // heavy enough to draw real current. 4 independent FMA accumulator chains keep the FP units busy.
    private const int Iter = 4_000_000;
    private const int Reps = 8;          // back-to-back timed reps per window; sustained load builds droop
    private const int WarmupReps = 3;    // discard: let the core ramp to boost + warm caches/pipeline first
    private const int BaselineProbes = 10;   // lock the healthy reference from this many early probes

    // Detection sensitivity (configurable via --slow-pct / --slow-persist). Default: a probe must be >=5%
    // slower than baseline (single-core random load barely heats the package, so thermal drift is small and
    // best-of-8 measurement noise is ~0.2% - 5% sits well clear of both) for this many consecutive windows.
    private readonly double _slowFactor;
    private readonly int _persistRequired;

    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
    private static readonly bool UseFma = Fma.IsSupported && Avx.IsSupported;

    // Keeps the kernel result observable so the JIT cannot optimise the loop away.
    public static double Sink;

    private readonly ErrorLogger _logger;
    private readonly ulong _affinity;
    private readonly List<double> _early = new();
    private double _baselineNs = -1;     // set once we have BaselineProbes samples
    private int _consecutiveSlow;
    private bool _reported;              // log a sustained slowdown only once per run (no spam)
    private int _probeNo;

    public PerfProbe(ErrorLogger logger, ulong affinity, double slowPct = 5.0, int persistRequired = 3)
    {
        _logger = logger;
        _affinity = affinity;
        _slowFactor = 1.0 + Math.Max(0.5, slowPct) / 100.0; // floor 0.5% so noise can't trip it
        _persistRequired = Math.Max(1, persistRequired);
    }

    /// <summary>Human-readable summary of the active detection settings (for the startup banner).</summary>
    public string SettingsDescription => $"flag when >={(_slowFactor - 1) * 100:0.#}% slower than baseline for {_persistRequired} consecutive probes";

    /// <summary>True if the probe can run (we have a single-core affinity to pin to).</summary>
    public bool Enabled => _affinity != 0UL;

    /// <summary>
    /// Take one probe sample. The CALLER must have suspended the y-cruncher worker first, so the core is
    /// free and the timing reflects the probe's own work alone. Runs on the calling (duty) thread, pinned
    /// to the core under test for the duration. Cheap (~tens of ms) and allocation-free.
    /// </summary>
    public void Measure(int pinnedCore)
    {
        if (!Enabled) return;

        ulong prev = Native.PinCurrentThread(_affinity);
        try
        {
            for (int i = 0; i < WarmupReps; i++) Sink = Kernel();

            double bestNs = double.MaxValue;
            for (int i = 0; i < Reps; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                double r = Kernel();
                long t1 = Stopwatch.GetTimestamp();
                Sink = r;
                double ns = (t1 - t0) * NsPerTick;
                if (ns < bestNs) bestNs = ns;
            }
            Evaluate(bestNs, pinnedCore);
        }
        finally { Native.RestoreThreadAffinity(prev); }
    }

    private void Evaluate(double bestNs, int pinnedCore)
    {
        _probeNo++;

        // Phase 1: characterise the healthy core. Lock the baseline from the MEDIAN of the first several
        // probes (median ignores both the cold-boost outlier on the fast side and a noisy sample on the
        // slow side). We don't judge anything until the baseline exists.
        if (_baselineNs < 0)
        {
            _early.Add(bestNs);
            if (_early.Count >= BaselineProbes)
            {
                _early.Sort();
                _baselineNs = _early[_early.Count / 2];
            }
            return;
        }

        double ratio = bestNs / _baselineNs;
        if (ratio >= _slowFactor)
        {
            _consecutiveSlow++;
            if (_consecutiveSlow >= _persistRequired && !_reported)
            {
                _reported = true;
                double pct = (ratio - 1) * 100;
                string where = pinnedCore >= 0 ? $"core {pinnedCore}" : "core";
                _logger.Log("SLOWDOWN", pinnedCore, "", "perf",
                    $"random probe {bestNs / 1000:F0}us vs baseline {_baselineNs / 1000:F0}us (+{pct:F0}%) on {where} " +
                    "-> sustained silent slowdown (clock stretching / throttling). Could be marginal CO or just heat.");
                Console.WriteLine($"    [SLOWDOWN] {where}: fixed-work probe +{pct:F0}% slower than baseline " +
                                  "-> silent performance drop (clock stretching or thermal throttling).");
            }
        }
        else
        {
            _consecutiveSlow = 0;
        }
    }

    // Fixed-work compute kernel. 4 independent FMA chains (acc = acc*m + c). The multipliers are chosen so
    // the accumulators stay bounded (no overflow / NaN) over millions of iterations, keeping every call
    // identical and deterministic. AVX2+FMA on Vector256<double>; scalar fallback if the CPU lacks them.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double Kernel() => UseFma ? KernelFma() : KernelScalar();

    private static double KernelFma()
    {
        var m0 = Vector256.Create(1.00000001);
        var m1 = Vector256.Create(0.99999999);
        var c = Vector256.Create(1e-8);
        var a0 = Vector256.Create(1.0);
        var a1 = Vector256.Create(1.0);
        var a2 = Vector256.Create(1.0);
        var a3 = Vector256.Create(1.0);
        for (int i = 0; i < Iter; i++)
        {
            a0 = Fma.MultiplyAdd(a0, m0, c);
            a1 = Fma.MultiplyAdd(a1, m1, c);
            a2 = Fma.MultiplyAdd(a2, m0, c);
            a3 = Fma.MultiplyAdd(a3, m1, c);
        }
        var s = Avx.Add(Avx.Add(a0, a1), Avx.Add(a2, a3));
        return s.GetElement(0) + s.GetElement(1) + s.GetElement(2) + s.GetElement(3);
    }

    private static double KernelScalar()
    {
        double a0 = 1.0, a1 = 1.0, a2 = 1.0, a3 = 1.0;
        for (int i = 0; i < Iter; i++)
        {
            a0 = a0 * 1.00000001 + 1e-8;
            a1 = a1 * 0.99999999 + 1e-8;
            a2 = a2 * 1.00000001 + 1e-8;
            a3 = a3 * 0.99999999 + 1e-8;
        }
        return a0 + a1 + a2 + a3;
    }
}
