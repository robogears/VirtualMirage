using System.Diagnostics;

namespace VirtualMirage.UI;

/// <summary>
/// The whole app's UI surface: a tray icon + context menu. Owns a hidden control
/// purely to marshal calls from background threads onto the UI thread.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _miStatus;
    private readonly ToolStripMenuItem _miUpdate;
    private readonly ToolStripMenuItem _miAutomation;
    private readonly Control _marshal;

    public event Action? CreateDisplayRequested;
    public event Action? RemoveDisplayRequested;
    public event Action? RunDiagnosticsRequested;
    public event Action<bool>? AutomationToggled;
    public event Action? OpenSettingsRequested;
    public event Action? UpdateMenuClicked;
    public event Action? QuitRequested;

    public TrayApp(bool automationEnabled)
    {
        _marshal = new Control();
        _ = _marshal.Handle; // force handle creation on the UI thread for later Invoke marshaling

        _miStatus = new ToolStripMenuItem("Status: starting…") { Enabled = false };
        _miAutomation = new ToolStripMenuItem("Automation enabled")
        {
            Checked = automationEnabled,
            CheckOnClick = true,
        };
        _miAutomation.CheckedChanged += (_, _) => AutomationToggled?.Invoke(_miAutomation.Checked);

        _miUpdate = Item("Check for updates", () => UpdateMenuClicked?.Invoke());

        var miCreate   = Item("Create virtual display now", () => CreateDisplayRequested?.Invoke());
        var miRemove   = Item("Remove virtual display now", () => RemoveDisplayRequested?.Invoke());
        var miDiag     = Item("Run detection diagnostics…", () => RunDiagnosticsRequested?.Invoke());
        var miSettings = Item("Settings…", () => OpenSettingsRequested?.Invoke());
        var miLogs     = Item("Open logs folder", OpenLogs);
        var miQuit     = Item("Quit", () => QuitRequested?.Invoke());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _miStatus,
            new ToolStripSeparator(),
            _miUpdate,
            new ToolStripSeparator(),
            _miAutomation,
            new ToolStripSeparator(),
            miCreate, miRemove,
            new ToolStripSeparator(),
            miDiag, miSettings, miLogs,
            new ToolStripSeparator(),
            miQuit,
        });

        _tray = new NotifyIcon
        {
            Icon = StatusIcons.For(automationEnabled ? AppStatus.Idle : AppStatus.Disabled),
            Text = "VirtualMirage",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (_, _) => OpenSettingsRequested?.Invoke();
    }

    private static ToolStripMenuItem Item(string text, Action onClick)
        => new(text, null, (_, _) => onClick());

    public void SetStatus(AppStatus status, string text) => RunOnUi(() =>
    {
        _miStatus.Text = $"Status: {text}";
        _tray.Icon = StatusIcons.For(status);
        string tip = $"VirtualMirage — {text}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    });

    public void SetUpdateMenu(string text, bool enabled) => RunOnUi(() =>
    {
        _miUpdate.Text = text;
        _miUpdate.Enabled = enabled;
    });

    public void SetAutomationChecked(bool on) => RunOnUi(() => _miAutomation.Checked = on);

    public void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info) => RunOnUi(() =>
    {
        try
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(4000);
        }
        catch (Exception ex) { Log.Error("balloon failed", ex); }
    });

    private void RunOnUi(Action a)
    {
        try
        {
            if (_marshal.IsDisposed) return;
            if (_marshal.InvokeRequired) _marshal.BeginInvoke(a);
            else a();
        }
        catch (Exception ex) { Log.Error("UI marshal failed", ex); }
    }

    private static void OpenLogs()
    {
        try
        {
            Paths.EnsureCreated();
            Process.Start(new ProcessStartInfo { FileName = Paths.LogsDir, UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("open logs failed", ex); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            try { _menu.Dispose(); } catch { }
            try { _marshal.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
