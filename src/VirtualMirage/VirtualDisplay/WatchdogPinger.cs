namespace VirtualMirage.VirtualDisplay;

/// <summary>
/// Keeps the SUDOVDA watchdog satisfied. SUDOVDA auto-removes orphaned virtual
/// displays if it stops receiving pings (default 3s timeout), which is our
/// dead-man's-switch if the app crashes. Mirrors Apollo's ping thread:
/// ping every Timeout/3 seconds, fail after 3 consecutive misses.
/// </summary>
public sealed class WatchdogPinger : IDisposable
{
    private readonly Func<bool> _ping;
    private readonly int _intervalMs;
    private readonly Action _onFail;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public WatchdogPinger(Func<bool> ping, int intervalMs, Action onFail)
    {
        _ping = ping;
        _intervalMs = Math.Max(250, intervalMs);
        _onFail = onFail;
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "SudoVDA-Ping" };
        _thread.Start();
    }

    private void Loop()
    {
        int fails = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_ping()) fails = 0;
                else if (++fails > 3)
                {
                    Log.Error("SUDOVDA watchdog ping failed 3x; signalling failure.");
                    try { _onFail(); } catch (Exception ex) { Log.Error("watchdog onFail handler threw", ex); }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("watchdog ping threw", ex);
            }

            if (_cts.Token.WaitHandle.WaitOne(_intervalMs)) return; // cancelled
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
