using System.Diagnostics;

namespace Rynez;

/// <summary>
/// Detects momentary system hitches/stutters ("brief stutter"): a high-priority thread wakes ~every 1ms
/// and measures how long it ACTUALLY slept. Normally ~1-2ms; if it suddenly measures e.g. 30ms,
/// the system failed to schedule any thread for 30ms = a stall/freeze the user feels as a stutter.
///
/// This is the symptom-level detector for the original "0.01-0.1s micro-freeze" goal. It is independent
/// of what is computing (works with our kernels, with y-cruncher, or while a real game runs).
///
/// Caveat: under all-core 100% load this thread is itself starved, so it will report scheduling-induced
/// hitches that are not hardware instability. It is most meaningful at low/moderate load (gaming, idle).
/// Needs timeBeginPeriod(1) for the 1ms wake to be precise.
/// </summary>
public sealed class StallMonitor : IDisposable
{
    private readonly ErrorLogger _logger;
    private readonly double _thresholdMs;
    private readonly CancellationToken _token;
    private readonly Func<int> _getCurrentCore;
    private Thread? _thread;

    public int HitchCount { get; private set; }
    public double WorstMs { get; private set; }

    public StallMonitor(ErrorLogger logger, double thresholdMs, CancellationToken token, Func<int> getCurrentCore)
    {
        _logger = logger;
        _thresholdMs = thresholdMs;
        _token = token;
        _getCurrentCore = getCurrentCore;
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        double last = sw.Elapsed.TotalMilliseconds;
        while (!_token.IsCancellationRequested)
        {
            _token.WaitHandle.WaitOne(1); // aim for ~1ms (precise with timeBeginPeriod(1))
            double now = sw.Elapsed.TotalMilliseconds;
            double interval = now - last;
            last = now;

            if (interval >= _thresholdMs)
            {
                HitchCount++;
                if (interval > WorstMs) WorstMs = interval;
                int core = _getCurrentCore();
                if (core >= 0)
                    // A hitch while one specific core is under test -> blame that core (counts as instability,
                    // triggers stop-on-detection + culprit banner).
                    _logger.Log("TRANSIENT", core, "", "hitch", $"system stutter {interval:F0}ms during core {core} test -> momentary stall");
                else
                    // No single core under test (idle / all-core) -> informational only, no stop.
                    _logger.Log("HITCH", -1, "", "", $"system stutter {interval:F0}ms (no thread ran this long)");
                Console.WriteLine($"  [HITCH] {interval:F0}ms system stall" + (core >= 0 ? $" (blamed on core {core})" : ""));
            }
        }
    }

    public void Dispose() => _thread?.Join(300);
}
