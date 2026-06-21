using VirtualMirage.Display;
using VirtualMirage.Interop;
using VirtualMirage.VirtualDisplay;

namespace VirtualMirage;

/// <summary>
/// Coordinates the full lifecycle: capture topology → create virtual display →
/// apply (4K120 + primary + disable others), and the reverse on deactivate.
/// Persists a crash-recovery snapshot while active. This is the worker the
/// Orchestrator (M5) drives from detection events; the tray drives it manually.
/// </summary>
public sealed class VirtualDisplaySession
{
    private readonly SudoVdaController _vda;
    private readonly DisplayManager _dm = new();
    private readonly Config _cfg;
    private readonly Action _onWatchdogFail;
    private readonly object _gate = new();

    private VirtualDisplayHandle? _handle;
    private DisplaySnapshot? _snapshot;
    private CancellationTokenSource? _enforcerCts;

    public bool IsActive { get { lock (_gate) return _handle is not null; } }
    public string? CurrentGdiName { get { lock (_gate) return _handle?.GdiName; } }

    public VirtualDisplaySession(SudoVdaController vda, Config cfg, Action onWatchdogFail)
    {
        _vda = vda;
        _cfg = cfg;
        _onWatchdogFail = onWatchdogFail;
    }

    public bool Activate(out string? gdiName)
    {
        lock (_gate)
        {
            gdiName = null;
            if (_handle is { } existing) { gdiName = existing.GdiName; Log.Info("Activate: already active."); return true; }

            // 1) Snapshot the clean topology BEFORE touching anything.
            var snap = _dm.Capture(_cfg.MonitorGuid);
            if (snap is null) { Log.Error("Activate: could not capture topology; aborting (no changes made)."); return false; }

            // 2) Create the virtual display.
            LUID? renderAdapter = _cfg.RenderAdapterLuid is { } v ? LUID.FromInt64(v) : null;
            var handle = _vda.CreateDisplay(
                (uint)_cfg.Display.Width, (uint)_cfg.Display.Height, (uint)_cfg.Display.RefreshHz,
                _cfg.MonitorGuid, _cfg.DeviceName, _cfg.SerialNumber, renderAdapter, _onWatchdogFail);

            if (handle is not { } h)
            {
                Log.Error("Activate: CreateDisplay (ADD) failed; aborting (no changes made).");
                return false;
            }
            if (string.IsNullOrEmpty(h.GdiName))
                Log.Info("Activate: display came up without an active name yet; Apply will activate it.");

            // 3) Persist crash-recovery state (snapshot + the guid to remove) before we change topology.
            snap.SaveToDisk();

            // 4) Apply mode + primary + disable-others (this also activates the display if it was inactive).
            bool applied = _dm.Apply(h, _cfg.Display, _cfg.SetPrimary, _cfg.DisableOtherMonitors);

            // Re-resolve the now-active name for status/logging.
            string? resolved = CcdInterop.FindGdiNameForTarget(h.AdapterLuid, h.TargetId);
            var finalHandle = string.IsNullOrEmpty(resolved) ? h : h with { GdiName = resolved! };

            _handle = finalHandle;
            _snapshot = snap;
            gdiName = finalHandle.GdiName;
            Log.Info($"Activate: virtual display active at '{finalHandle.GdiName}' (apply ok={applied}).");
            StartModeEnforcer(finalHandle);
            return true;
        }
    }

    public bool Deactivate()
    {
        lock (_gate)
        {
            StopModeEnforcer();
            if (_handle is not { } h)
            {
                Log.Info("Deactivate: not active; checking for persisted state to recover.");
                return RecoverFromDiskUnlocked();
            }

            // Restore the physical topology FIRST (so there's always an active display),
            // THEN remove the virtual device.
            bool restored = true;
            if (_snapshot is { } snap) restored = _dm.RestoreWithRetry(snap);
            else Log.Warn("Deactivate: no in-memory snapshot to restore.");

            bool removed = _vda.RemoveDisplay(h.MonitorGuid);

            // The CCD array restore above can fall back to USE_DATABASE_CURRENT (which loses refresh +
            // primary); explicitly re-apply each physical monitor's exact mode + primary once the
            // virtual display is gone. This is the fix for "360Hz drops to 120 / secondary becomes main".
            if (_snapshot is { } snap2)
            {
                Thread.Sleep(300); // let the topology settle after the virtual display is removed
                _dm.RestorePhysicalModes(snap2);
            }

            DisplaySnapshot.DeleteFromDisk();

            _handle = null;
            _snapshot = null;
            Log.Info($"Deactivate: restored={restored}, removed={removed}.");
            return restored && removed;
        }
    }

    /// <summary>On startup: if a session.state file exists we crashed mid-session — restore + remove orphan.</summary>
    public bool RecoverIfNeeded()
    {
        lock (_gate) return RecoverFromDiskUnlocked();
    }

    private bool RecoverFromDiskUnlocked()
    {
        var snap = DisplaySnapshot.LoadFromDisk();
        if (snap is null) return true; // nothing to recover

        Log.Warn($"Recovering from persisted session state (captured {snap.CapturedUtc:o}); removing orphan + restoring.");
        if (_vda.Open()) _vda.RemoveDisplay(snap.VirtualMonitorGuid); // watchdog may already have reaped it
        bool restored = _dm.RestoreWithRetry(snap);
        Thread.Sleep(300);
        _dm.RestorePhysicalModes(snap); // faithfully restore refresh/position/primary on crash recovery too
        DisplaySnapshot.DeleteFromDisk();
        Log.Info($"Recovery complete (restored={restored}).");
        return restored;
    }

    private void StartModeEnforcer(VirtualDisplayHandle h)
    {
        StopModeEnforcer();
        var cts = new CancellationTokenSource();
        _enforcerCts = cts;
        var res = _cfg.Display;
        Task.Run(() =>
        {
            // Virtual Desktop overrides the virtual display's resolution to its own stream setting
            // when it connects (and can re-apply it later). Re-assert the requested mode for the
            // whole session, correcting only when it actually drifted (so there's no needless flicker).
            while (!cts.IsCancellationRequested)
            {
                if (cts.Token.WaitHandle.WaitOne(2000)) return; // cancelled
                try
                {
                    VirtualDisplayHandle cur;
                    lock (_gate)
                    {
                        if (_handle is not { } hh || hh.MonitorGuid != h.MonitorGuid) return;
                        cur = hh;
                    }
                    _dm.EnforceMode(cur, res);
                }
                catch (Exception ex) { Log.Error("mode enforcer iteration failed", ex); }
            }
        });
    }

    private void StopModeEnforcer()
    {
        try { _enforcerCts?.Cancel(); } catch { }
        _enforcerCts = null;
    }
}
