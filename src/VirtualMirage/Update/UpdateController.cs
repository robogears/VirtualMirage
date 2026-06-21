using System.Diagnostics;
using VirtualMirage.UI;

namespace VirtualMirage.Update;

/// <summary>
/// Drives the single tray "update" menu item through the state machine
/// (idle/check -> available -> downloading% -> ready -> restarting), mirroring
/// the renderer button state machine from updater.md.
/// </summary>
public sealed class UpdateController
{
    private enum St { Idle, Checking, Available, Downloading, Ready, Restarting }

    private readonly TrayApp _tray;
    private readonly object _gate = new();
    private St _state = St.Idle;
    private UpdateCheckResult? _available;
    private string? _downloadedPath;

    public UpdateController(TrayApp tray)
    {
        _tray = tray;
        _tray.UpdateMenuClicked += () => _ = OnClickAsync();
    }

    /// <summary>Silent check on launch; only surfaces a notice if an update is available.</summary>
    public async Task StartLaunchCheckAsync()
    {
        var r = await Updater.CheckAsync();
        lock (_gate)
        {
            if (r.Status == UpdateStatus.Available)
            {
                _available = r;
                _state = St.Available;
                _tray.SetUpdateMenu($"Update available: {r.Version} — Download", true);
                _tray.Notify("VirtualMirage", $"Update available: {r.Version}. Open the tray menu to download.");
            }
            else
            {
                _state = St.Idle;
                _tray.SetUpdateMenu("Check for updates", true);
            }
        }
    }

    private async Task OnClickAsync()
    {
        St s;
        lock (_gate) s = _state;
        try
        {
            switch (s)
            {
                case St.Idle: await DoCheckAsync(); break;
                case St.Available: await DoDownloadAsync(); break;
                case St.Ready: DoApply(); break;
                default: break; // checking / downloading / restarting: ignore clicks
            }
        }
        catch (Exception ex) { Log.Error("update click handler failed", ex); }
    }

    private async Task DoCheckAsync()
    {
        lock (_gate) _state = St.Checking;
        _tray.SetUpdateMenu("Checking for updates…", false);

        var r = await Updater.CheckAsync();
        lock (_gate)
        {
            switch (r.Status)
            {
                case UpdateStatus.Available:
                    _available = r; _state = St.Available;
                    _tray.SetUpdateMenu($"Update available: {r.Version} — Download", true);
                    break;
                case UpdateStatus.UpToDate:
                    _state = St.Idle;
                    _tray.SetUpdateMenu("Up to date ✓", true);
                    RevertIdleLater();
                    break;
                default:
                    _state = St.Idle;
                    _tray.SetUpdateMenu("Update check failed", true);
                    RevertIdleLater();
                    break;
            }
        }
    }

    private async Task DoDownloadAsync()
    {
        UpdateCheckResult? r;
        lock (_gate) r = _available;
        if (r?.DownloadUrl is null) return;

        // No self-install (non-Windows) or the asset wasn't an .exe -> open the release page instead.
        if (!Updater.CanSelfInstall() || !r.DownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try { Process.Start(new ProcessStartInfo { FileName = r.ReleaseUrl ?? r.DownloadUrl, UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("open release page failed", ex); }
            return;
        }

        lock (_gate) _state = St.Downloading;
        _tray.SetUpdateMenu("Starting download…", false);

        var progress = new Progress<(long d, long t)>(p =>
        {
            // Ignore late progress once we've left the Downloading state — otherwise a trailing
            // "100%" callback can overwrite the "Restart to apply" text after the download finished.
            lock (_gate) { if (_state != St.Downloading) return; }
            string txt = p.t > 0
                ? $"Downloading {(int)(p.d * 100 / p.t)}%"
                : $"Downloading {p.d / 1024 / 1024} MB";
            _tray.SetUpdateMenu(txt, false);
        });

        // Run the download on a worker thread so the 68 MB transfer never freezes the UI thread.
        string? path = await Task.Run(() => Updater.DownloadAsync(r.DownloadUrl, progress));
        lock (_gate)
        {
            if (path is null)
            {
                _state = St.Available;
                _tray.SetUpdateMenu("Download failed — retry", true);
            }
            else
            {
                _downloadedPath = path;
                _state = St.Ready;
                _tray.SetUpdateMenu("Restart to apply update", true);
            }
        }
    }

    private void DoApply()
    {
        string? path;
        lock (_gate)
        {
            if (_downloadedPath is null) return;
            path = _downloadedPath;
            _state = St.Restarting;
        }
        _tray.SetUpdateMenu("Installing update…", false);
        if (Updater.ApplyUpdate(path))
        {
            Application.Exit(); // the installer closes us, installs, and relaunches
        }
        else
        {
            // Installer didn't launch (UAC declined?) — stay ready so the user can retry.
            lock (_gate) _state = St.Ready;
            _tray.SetUpdateMenu("Restart to apply update", true);
        }
    }

    private void RevertIdleLater()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            lock (_gate)
            {
                if (_state == St.Idle) _tray.SetUpdateMenu("Check for updates", true);
            }
        });
    }
}
