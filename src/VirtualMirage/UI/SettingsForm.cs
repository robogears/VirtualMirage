using System.Drawing;

namespace VirtualMirage.UI;

/// <summary>Minimal settings dialog over the Config object (config.json is also hand-editable).</summary>
public sealed class SettingsForm : Form
{
    private readonly Config _cfg;

    private readonly NumericUpDown _w = Num(640, 7680);
    private readonly NumericUpDown _h = Num(480, 4320);
    private readonly NumericUpDown _hz = Num(24, 500);
    private readonly CheckBox _setPrimary = new() { Text = "Make the virtual display primary", AutoSize = true };
    private readonly CheckBox _disableOthers = new() { Text = "Disable my physical monitors while in VR", AutoSize = true };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly NumericUpDown _dbConnect = Num(0, 30000);
    private readonly NumericUpDown _dbDisconnect = Num(0, 60000);
    private readonly CheckBox _skipApollo = new() { Text = "Skip if a virtual display is already active (Apollo/Sunshine)", AutoSize = true };
    private readonly CheckBox _startWithWindows = new() { Text = "Start VirtualMirage when I sign in", AutoSize = true };
    private readonly CheckBox _updateOnLaunch = new() { Text = "Check for updates on launch", AutoSize = true };

    public SettingsForm(Config cfg)
    {
        _cfg = cfg;
        Text = "VirtualMirage Settings";
        Icon = IconArt.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(440, 470);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, AutoSize = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddHeader(root, "Virtual display");
        AddRow(root, "Resolution", Flow(_w, new Label { Text = "x", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(2, 6, 2, 0) }, _h));
        AddRow(root, "Refresh (Hz)", _hz);
        AddSpan(root, _setPrimary);
        AddSpan(root, _disableOthers);

        AddHeader(root, "Detection");
        _mode.Items.AddRange(new object[] { "auto", "event", "ports" });
        AddRow(root, "Mode", _mode);
        AddRow(root, "Connect debounce (ms)", _dbConnect);
        AddRow(root, "Disconnect debounce (ms)", _dbDisconnect);
        AddSpan(root, _skipApollo);

        AddHeader(root, "Startup");
        AddSpan(root, _startWithWindows);
        AddSpan(root, _updateOnLaunch);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        ok.Click += (_, _) => Persist();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(root);
        Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;

        Load += (_, _) => LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        _w.Value = Clamp(_cfg.Display.Width, _w);
        _h.Value = Clamp(_cfg.Display.Height, _h);
        _hz.Value = Clamp(_cfg.Display.RefreshHz, _hz);
        _setPrimary.Checked = _cfg.SetPrimary;
        _disableOthers.Checked = _cfg.DisableOtherMonitors;
        _mode.SelectedItem = (_cfg.Detection.Mode ?? "auto").ToLowerInvariant();
        if (_mode.SelectedIndex < 0) _mode.SelectedItem = "auto";
        _dbConnect.Value = Clamp(_cfg.Detection.DebounceConnectMs, _dbConnect);
        _dbDisconnect.Value = Clamp(_cfg.Detection.DebounceDisconnectMs, _dbDisconnect);
        _skipApollo.Checked = _cfg.SkipIfApolloActive;
        _startWithWindows.Checked = Autostart.IsEnabled();
        _updateOnLaunch.Checked = _cfg.UpdateCheckOnLaunch;
    }

    private void Persist()
    {
        _cfg.Display.Width = (int)_w.Value;
        _cfg.Display.Height = (int)_h.Value;
        _cfg.Display.RefreshHz = (int)_hz.Value;
        _cfg.SetPrimary = _setPrimary.Checked;
        _cfg.DisableOtherMonitors = _disableOthers.Checked;
        _cfg.Detection.Mode = (_mode.SelectedItem as string) ?? "auto";
        _cfg.Detection.DebounceConnectMs = (int)_dbConnect.Value;
        _cfg.Detection.DebounceDisconnectMs = (int)_dbDisconnect.Value;
        _cfg.SkipIfApolloActive = _skipApollo.Checked;
        _cfg.StartWithWindows = _startWithWindows.Checked;
        _cfg.UpdateCheckOnLaunch = _updateOnLaunch.Checked;
        _cfg.Save();
        Autostart.Set(_startWithWindows.Checked);
        Log.Info("Settings saved from dialog.");
    }

    // ---- tiny layout helpers ----
    private static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Width = 90, Increment = 1 };
    private static decimal Clamp(int v, NumericUpDown n) => Math.Min(Math.Max(v, (int)n.Minimum), (int)n.Maximum);

    private static Control Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        f.Controls.AddRange(controls);
        return f;
    }

    private static void AddHeader(TableLayoutPanel t, string text)
    {
        var l = new Label { Text = text, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 10, 0, 4) };
        int r = t.RowCount++;
        t.Controls.Add(l, 0, r);
        t.SetColumnSpan(l, 2);
    }

    private static void AddRow(TableLayoutPanel t, string label, Control control)
    {
        int r = t.RowCount++;
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) }, 0, r);
        t.Controls.Add(control, 1, r);
    }

    private static void AddSpan(TableLayoutPanel t, Control control)
    {
        int r = t.RowCount++;
        control.Margin = new Padding(0, 4, 0, 0);
        t.Controls.Add(control, 0, r);
        t.SetColumnSpan(control, 2);
    }
}
