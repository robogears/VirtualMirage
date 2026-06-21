namespace VirtualMirage.Detection;

/// <summary>
/// Polls the VD signals on a background thread and raises debounced Connected/Disconnected
/// events. Detection mode (auto|event|ports) and debounce timings come from config.
/// </summary>
public sealed class VdSessionDetector : IDisposable
{
    private readonly Config _cfg;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    private bool _connected;
    private bool _lastRaw;
    private long _rawSinceTick;

    public event Action? Connected;
    public event Action? Disconnected;
    public bool IsConnected => _connected;

    public VdSessionDetector(Config cfg) => _cfg = cfg;

    public void Start()
    {
        _rawSinceTick = Environment.TickCount64;
        _thread = new Thread(Loop) { IsBackground = true, Name = "VdSessionDetector" };
        _thread.Start();
        Log.Info($"Detector started (mode={_cfg.Detection.Mode}, poll={_cfg.Detection.PollIntervalMs}ms, " +
                 $"debounce {_cfg.Detection.DebounceConnectMs}/{_cfg.Detection.DebounceDisconnectMs}ms).");
    }

    /// <summary>Evaluate the instantaneous "session active" signal per the configured mode.</summary>
    public bool EvaluateRaw(out string detail)
    {
        if (!VdSignals.IsStreamerRunning()) { detail = "streamer not running"; return false; }

        string mode = (_cfg.Detection.Mode ?? "auto").ToLowerInvariant();
        int[] ports = _cfg.Detection.StreamerPorts is { Length: > 0 } p ? p : VdSignals.DefaultPorts;

        bool portsActive = mode is "ports" or "auto" && VdSignals.HasLanPeer(ports);
        bool eventActive = mode is "event" or "auto" && VdSignals.TryEventPulse(200);

        bool active = mode switch
        {
            "ports" => portsActive,
            "event" => eventActive,
            _ => portsActive || eventActive, // auto
        };
        detail = $"streamer=1 ports={(portsActive ? 1 : 0)} event={(eventActive ? 1 : 0)} -> {(active ? "ACTIVE" : "idle")}";
        return active;
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                bool raw = EvaluateRaw(out string detail);
                long now = Environment.TickCount64;
                if (raw != _lastRaw) { _lastRaw = raw; _rawSinceTick = now; }
                long stableMs = now - _rawSinceTick;

                if (!_connected && raw && stableMs >= _cfg.Detection.DebounceConnectMs)
                {
                    _connected = true;
                    Log.Info($"VD session CONNECTED ({detail}).");
                    SafeRaise(Connected);
                }
                else if (_connected && !raw && stableMs >= _cfg.Detection.DebounceDisconnectMs)
                {
                    _connected = false;
                    Log.Info($"VD session DISCONNECTED ({detail}).");
                    SafeRaise(Disconnected);
                }
            }
            catch (Exception ex) { Log.Error("detector loop error", ex); }

            if (_cts.Token.WaitHandle.WaitOne(Math.Max(100, _cfg.Detection.PollIntervalMs))) return;
        }
    }

    private static void SafeRaise(Action? a)
    {
        try { a?.Invoke(); } catch (Exception ex) { Log.Error("detector event handler threw", ex); }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
