using VirtualMirage.Detection;
using VirtualMirage.Display;
using VirtualMirage.UI;

namespace VirtualMirage;

/// <summary>
/// The state machine: owns the detector and turns its debounced connect/disconnect
/// events into session activate/deactivate, gated by config and an Apollo/Sunshine
/// contention guard. Tray status reflects the current state.
/// </summary>
public sealed class Orchestrator : IDisposable
{
    private readonly Config _cfg;
    private readonly VirtualDisplaySession _session;
    private readonly TrayApp _tray;
    private readonly VdSessionDetector _detector;
    private readonly object _gate = new();

    public Orchestrator(Config cfg, VirtualDisplaySession session, TrayApp tray)
    {
        _cfg = cfg;
        _session = session;
        _tray = tray;
        _detector = new VdSessionDetector(cfg);
        _detector.Connected += OnConnected;
        _detector.Disconnected += OnDisconnected;
    }

    public void Start() => _detector.Start();

    private void OnConnected()
    {
        lock (_gate)
        {
            if (!_cfg.AutomationEnabled)
            {
                _tray.SetStatus(AppStatus.Connected, "VR connected (automation off)");
                return;
            }
            if (_session.IsActive)
            {
                _tray.SetStatus(AppStatus.Active, $"{_cfg.Display} (already active)");
                return;
            }
            if (_cfg.SkipIfApolloActive && GdiInterop.HasActiveDisplayMatching("SudoMaker"))
            {
                Log.Warn("A SudoMaker virtual display is already active (Apollo/Sunshine stream?); skipping to avoid contention.");
                _tray.SetStatus(AppStatus.Connected, "VR connected (skipped: display in use)");
                _tray.Notify("VirtualMirage", "VR connected, but a virtual display is already active (Apollo?). Skipping.", ToolTipIcon.Warning);
                return;
            }

            _tray.SetStatus(AppStatus.Connected, $"Activating {_cfg.Display}…");
            if (_session.Activate(out var name))
            {
                _tray.SetStatus(AppStatus.Active, $"{_cfg.Display} on {name}");
                _tray.Notify("VirtualMirage", $"VR connected — {_cfg.Display} virtual display active.");
            }
            else
            {
                _tray.SetStatus(AppStatus.Error, "Activation failed");
                _tray.Notify("VirtualMirage", "VR connected but activation failed (see logs).", ToolTipIcon.Error);
            }
        }
    }

    private void OnDisconnected()
    {
        lock (_gate)
        {
            if (!_session.IsActive)
            {
                SetIdle();
                return;
            }
            _tray.SetStatus(AppStatus.Connected, "Restoring displays…");
            bool ok = _session.Deactivate();
            SetIdle();
            _tray.Notify("VirtualMirage",
                ok ? "VR disconnected — displays restored." : "VR disconnected — restore had issues (see logs).",
                ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
    }

    private void SetIdle()
        => _tray.SetStatus(_cfg.AutomationEnabled ? AppStatus.Idle : AppStatus.Disabled,
                           _cfg.AutomationEnabled ? "Idle (waiting for VR)" : "Disabled");

    public void Dispose() => _detector.Dispose();
}
