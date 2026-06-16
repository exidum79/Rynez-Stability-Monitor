namespace Rynez;

/// <summary>
/// Writes a "last alive" breadcrumb to disk once per second, OVERWRITING the same file.
///
/// Why: an uncorrectable CPU error reboots the machine instantly and (on many systems) leaves
/// NOTHING in the Windows event log. So we leave our own trace: every second we overwrite a tiny
/// file with the current timestamp + which core is being tested. After a hard reboot, the file's
/// contents = the state ~1 second before the machine died = the core that was running when it died.
///
/// This is the whole point of the tool: WE are the recorder, not Windows.
/// Pair it with single-core rotation (--mode heavy) so the breadcrumb names ONE core = the culprit.
/// </summary>
public sealed class CrashTrail : IDisposable
{
    private readonly string _path;
    private Func<string>? _state;
    private CancellationToken _token;
    private Thread? _thread;
    private volatile bool _sealed; // once clean exit is written, stop periodic overwrites

    public CrashTrail(string logDir)
    {
        Directory.CreateDirectory(logDir);
        _path = Path.Combine(logDir, "lastalive.txt");
    }

    /// <summary>Read the previous run's last breadcrumb (call BEFORE Start overwrites it). Null if none.</summary>
    public string? ReadPrevious()
    {
        try { return File.Exists(_path) ? File.ReadAllText(_path) : null; }
        catch { return null; }
    }

    public void Start(Func<string> state, CancellationToken token)
    {
        _state = state;
        _token = token;
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_token.IsCancellationRequested)
        {
            Write();
            _token.WaitHandle.WaitOne(1000); // every 1s
        }
        // No final write here: on clean shutdown MarkCleanExit() writes the authoritative last state.
    }

    private void Write()
    {
        if (_sealed) return;
        try
        {
            string text = $"last_alive={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\n{_state?.Invoke()}\n";
            File.WriteAllText(_path, text);
        }
        catch { /* ignore transient IO errors */ }
    }

    /// <summary>Mark a clean shutdown so next run knows the previous run ended normally.</summary>
    public void MarkCleanExit()
    {
        _sealed = true; // stop any further periodic overwrites
        try { File.WriteAllText(_path, $"last_alive={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\nCLEAN_EXIT\n"); }
        catch { }
    }

    public void Dispose() => _thread?.Join(300);
}
