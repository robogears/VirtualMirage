namespace VirtualMirage.Detection;

/// <summary>
/// Calibration tool: samples every signal at 250ms for a window while the user connects and
/// disconnects the headset once, writing a timestamped report. The transitions reveal which
/// signal(s) reliably flip on this machine so the detection rule can be locked.
/// </summary>
public static class DetectionDiagnostics
{
    public static string Run(Config cfg, int seconds = 60, CancellationToken ct = default)
    {
        VirtualMirage.Paths.EnsureCreated();
        string path = Path.Combine(VirtualMirage.Paths.LogsDir, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        using var sw = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));
        void W(string s) { sw.WriteLine(s); sw.Flush(); Log.Info("[diag] " + s); }

        int[] ports = cfg.Detection.StreamerPorts is { Length: > 0 } p ? p : VdSignals.DefaultPorts;

        W($"=== VD detection diagnostics - {seconds}s window ===");
        W("ACTION REQUIRED: within this window, connect your headset via Virtual Desktop, wait a few seconds,");
        W("then disconnect. Watch for the '>>> raw active changed' lines.");
        W($"Ports watched: {string.Join(",", ports)}");
        W("time | streamer | mmf | eventPulse | lanPeers | rawAuto");

        long endTick = Environment.TickCount64 + seconds * 1000L;
        bool? lastRaw = null;
        while (Environment.TickCount64 < endTick && !ct.IsCancellationRequested)
        {
            bool streamer = VdSignals.IsStreamerRunning();
            bool mmf = VdSignals.MmfExists();
            bool evt = streamer && VdSignals.TryEventPulse(50);
            var peers = VdSignals.GetVdLanPeers(ports);
            bool raw = streamer && (peers.Count > 0 || evt);

            string peerStr = peers.Count == 0 ? "-" : string.Join(",", peers.Select(x => $"{x.LocalPort}<-{x.Remote}"));
            if (lastRaw != raw) { W($">>> raw active changed to {(raw ? "ACTIVE" : "idle")}"); lastRaw = raw; }
            W($"{DateTime.Now:HH:mm:ss.fff} | {(streamer ? 1 : 0)} | {(mmf ? 1 : 0)} | {(evt ? 1 : 0)} | {peerStr} | {(raw ? "ACTIVE" : "idle")}");

            if (ct.WaitHandle.WaitOne(250)) break;
        }

        W("=== diagnostics done ===");
        return path;
    }
}
