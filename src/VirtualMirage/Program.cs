using VirtualMirage.Detection;
using VirtualMirage.Display;
using VirtualMirage.Interop;
using VirtualMirage.UI;
using VirtualMirage.Update;
using VirtualMirage.VirtualDisplay;

namespace VirtualMirage;

internal static class Program
{
    private static Mutex? _instanceMutex;

    [STAThread]
    private static void Main(string[] args)
    {
        // Carry over data from the former app name (AutoVRVD) before anything touches the paths.
        Paths.MigrateLegacyDataIfNeeded();

        // Headless diagnostics: create a 4K120 virtual display, confirm it appears, remove it.
        if (args.Length > 0 && args[0].Equals("--selftest-vda", StringComparison.OrdinalIgnoreCase))
        {
            SelfTestVda();
            return;
        }
        if (args.Length > 0 && args[0].Equals("--selftest-restore", StringComparison.OrdinalIgnoreCase))
        {
            SelfTestRestore();
            return;
        }
        if (args.Length > 0 && args[0].Equals("--selftest-fullcycle", StringComparison.OrdinalIgnoreCase))
        {
            SelfTestFullCycle();
            return;
        }
        if (args.Length > 0 && args[0].Equals("--diagnose", StringComparison.OrdinalIgnoreCase))
        {
            Log.Init();
            int secs = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 60;
            var report = DetectionDiagnostics.Run(Config.Load(), secs);
            Log.Info($"Diagnostics report written to: {report}");
            return;
        }
        if (args.Length > 0 && args[0].Equals("--selftest-settings", StringComparison.OrdinalIgnoreCase))
        {
            Log.Init();
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                using var f = new SettingsForm(Config.Load());
                _ = f.Handle; // force control/layout creation
                Log.Info("SettingsForm constructed OK.");
            }
            catch (Exception ex) { Log.Error("SettingsForm construction failed", ex); }
            return;
        }
        if (args.Length > 0 && args[0].Equals("--selftest-update", StringComparison.OrdinalIgnoreCase))
        {
            Log.Init();
            var r = Updater.CheckAsync().GetAwaiter().GetResult();
            Log.Info($"Current version {Updater.CurrentVersion()}, canSelfInstall={Updater.CanSelfInstall()}.");
            Log.Info($"Update check -> status={r.Status}, version={r.Version}, url={r.DownloadUrl}, msg={r.Message}");
            return;
        }
        if (args.Length > 0 && args[0].Equals("--diag-enforce", StringComparison.OrdinalIgnoreCase))
        {
            DiagEnforce();
            return;
        }
        if (args.Length > 0 && args[0].Equals("--diag-restore", StringComparison.OrdinalIgnoreCase))
        {
            DiagRestore();
            return;
        }
        // Installer hooks (invoked by setup.exe as the signed-in user to manage per-user autostart).
        if (args.Length > 0 && args[0].Equals("--set-autostart", StringComparison.OrdinalIgnoreCase))
        {
            Log.Init(); Autostart.Set(true); return;
        }
        if (args.Length > 0 && args[0].Equals("--unset-autostart", StringComparison.OrdinalIgnoreCase))
        {
            Log.Init(); Autostart.Set(false); return;
        }
        // Dev tool: (re)generate the multi-size app icon + a 256px preview PNG.
        if (args.Length > 0 && args[0].Equals("--make-icon", StringComparison.OrdinalIgnoreCase))
        {
            string outIco = args.Length > 1 ? args[1] : "VirtualMirage.ico";
            File.WriteAllBytes(outIco, IconArt.BuildIco(new[] { 16, 24, 32, 48, 64, 128, 256 }));
            using (var preview = IconArt.RenderLogo(256)) preview.Save(Path.ChangeExtension(outIco, ".preview.png"));
            // Magnified montage of the actual small renders (nearest-neighbour) to judge legibility.
            int[] small = { 16, 24, 32, 48 };
            int pad = 8, scale = 6, w = pad, h = 48 * scale + pad * 2;
            foreach (var sz in small) w += sz * scale + pad;
            using (var montage = new System.Drawing.Bitmap(w, h))
            using (var mg = System.Drawing.Graphics.FromImage(montage))
            {
                mg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                mg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                mg.Clear(System.Drawing.Color.FromArgb(40, 40, 48));
                int x = pad;
                foreach (var sz in small)
                {
                    using var b = IconArt.RenderLogo(sz);
                    mg.DrawImage(b, x, pad, sz * scale, sz * scale);
                    x += sz * scale + pad;
                }
                montage.Save(Path.ChangeExtension(outIco, ".montage.png"));
            }
            return;
        }

        // Single instance.
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\VirtualMirage_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("VirtualMirage is already running — look for its icon in the system tray.",
                "VirtualMirage", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Log.Init();

        // DPI + visual styles configured in code (manifest deliberately omits dpiAware).
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => Log.Error("Unhandled UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled domain exception", e.ExceptionObject as Exception);

        var cfg = Config.Load();
        Log.Info($"Config: display={cfg.Display}, monitorGuid={cfg.MonitorGuid}, " +
                 $"setPrimary={cfg.SetPrimary}, disableOthers={cfg.DisableOtherMonitors}, automation={cfg.AutomationEnabled}");

        using var tray = new TrayApp(cfg.AutomationEnabled);
        using var vda = new SudoVdaController();

        void OnWatchdogFail()
        {
            Log.Error("Watchdog failed — SUDOVDA may have removed the virtual display.");
            tray.SetStatus(AppStatus.Error, "Watchdog failed");
            tray.Notify("VirtualMirage", "SUDOVDA watchdog failed; the virtual display may have been removed.", ToolTipIcon.Warning);
        }

        var session = new VirtualDisplaySession(vda, cfg, OnWatchdogFail);
        var orchestrator = new Orchestrator(cfg, session, tray);

        // Crash recovery first (restore desktop + remove orphan if a prior run died mid-session),
        // THEN start the detector so we never activate while still recovering.
        Task.Run(() =>
        {
            try { session.RecoverIfNeeded(); }
            catch (Exception ex) { Log.Error("startup recovery failed", ex); }
            finally { orchestrator.Start(); }
        });

        // In-app updater: silent launch check (surfaces a tray notice only if an update exists).
        var updateController = new UpdateController(tray);
        if (cfg.UpdateCheckOnLaunch)
            Task.Run(() => updateController.StartLaunchCheckAsync());

        // --- Manual control. "Create" runs the full activation (create + 4K120 + primary +
        //     disable others per config); "Remove" restores the desktop + removes the display.
        //     M4 adds detection, M5 wires it to drive these automatically on VD connect/disconnect. ---
        tray.CreateDisplayRequested += () => Task.Run(() =>
        {
            try
            {
                tray.SetStatus(AppStatus.Connected, "Activating virtual display…");
                if (session.Activate(out string? name))
                {
                    tray.SetStatus(AppStatus.Active, $"{cfg.Display} on {name}");
                    tray.Notify("VirtualMirage", $"Virtual display active: {name}.");
                }
                else
                {
                    tray.SetStatus(AppStatus.Error, "Activate failed");
                    tray.Notify("VirtualMirage", "Failed to activate the virtual display (see logs).", ToolTipIcon.Error);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Activate threw", ex);
                tray.SetStatus(AppStatus.Error, "Activate error");
            }
        });

        tray.RemoveDisplayRequested += () => Task.Run(() =>
        {
            try
            {
                tray.SetStatus(AppStatus.Connected, "Restoring displays…");
                bool ok = session.Deactivate();
                tray.SetStatus(cfg.AutomationEnabled ? AppStatus.Idle : AppStatus.Disabled,
                    ok ? "Idle (waiting for VR)" : "Restore issue");
                tray.Notify("VirtualMirage", ok ? "Displays restored; virtual display removed." : "Restore had issues (see logs).",
                    ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            catch (Exception ex) { Log.Error("Deactivate threw", ex); }
        });

        tray.RunDiagnosticsRequested += () => Task.Run(() =>
        {
            try
            {
                tray.Notify("VirtualMirage", "Detection diagnostics started (60s). Connect, wait, then disconnect your headset now.");
                string report = DetectionDiagnostics.Run(cfg, 60);
                tray.Notify("VirtualMirage", "Diagnostics complete — opening report.");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = report, UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error("diagnostics failed", ex); }
        });
        bool settingsOpen = false;
        tray.OpenSettingsRequested += () =>
        {
            if (settingsOpen) return;
            try
            {
                settingsOpen = true;
                using var form = new SettingsForm(cfg);
                form.ShowDialog();
            }
            catch (Exception ex) { Log.Error("settings dialog failed", ex); }
            finally { settingsOpen = false; }
        };
        tray.AutomationToggled += on =>
        {
            cfg.AutomationEnabled = on;
            cfg.Save();
            Log.Info($"Automation toggled -> {on}");
            tray.SetStatus(on ? AppStatus.Idle : AppStatus.Disabled,
                on ? "Idle (waiting for VR)" : "Disabled");
        };
        tray.QuitRequested += () =>
        {
            Log.Info("Quit requested from tray.");
            Application.Exit();
        };

        tray.SetStatus(cfg.AutomationEnabled ? AppStatus.Idle : AppStatus.Disabled,
            cfg.AutomationEnabled ? "Idle (waiting for VR)" : "Disabled");

        Log.Info("Tray ready; entering message loop.");
        Application.Run(tray);

        // Stop the detector first so it can't re-activate during shutdown, then restore if needed.
        try { orchestrator.Dispose(); } catch (Exception ex) { Log.Error("orchestrator dispose failed", ex); }
        try
        {
            if (session.IsActive)
            {
                Log.Info("Exiting while active — restoring displays first.");
                session.Deactivate();
            }
        }
        catch (Exception ex) { Log.Error("exit deactivate failed", ex); }

        Log.Info("=== VirtualMirage exiting ===");
        try { _instanceMutex?.ReleaseMutex(); } catch { }
    }

    private static void SelfTestVda()
    {
        Log.Init();
        Log.Info("==== SUDOVDA SELFTEST ====");
        var cfg = Config.Load();

        using var vda = new SudoVdaController();
        if (!vda.Open())
        {
            Log.Error("SELFTEST: could not open SUDOVDA device. Aborting.");
            return;
        }

        vda.TryGetProtocolVersion(out var pv);
        vda.TryGetWatchdog(out var wd);
        Log.Info($"SELFTEST: protocol {pv.Major}.{pv.Minor}.{pv.Incremental} (test={pv.TestBuild != 0}); " +
                 $"watchdog timeout={wd.Timeout}s, countdown={wd.Countdown}s");
        Log.Info("SELFTEST: monitors BEFORE:\n" + GdiInterop.DescribeAll());

        bool watchdogFailed = false;
        var handle = vda.CreateDisplay(
            (uint)cfg.Display.Width, (uint)cfg.Display.Height, (uint)cfg.Display.RefreshHz,
            cfg.MonitorGuid, cfg.DeviceName, cfg.SerialNumber, null, () => watchdogFailed = true);

        if (handle is { } h) Log.Info($"SELFTEST: created -> {h}");
        else Log.Error("SELFTEST: CreateDisplay returned null.");

        Thread.Sleep(1500);
        Log.Info("SELFTEST: monitors AFTER CREATE:\n" + GdiInterop.DescribeAll());

        bool removed = vda.RemoveDisplay(cfg.MonitorGuid);
        Log.Info($"SELFTEST: remove -> {removed}");
        Thread.Sleep(800);
        Log.Info("SELFTEST: monitors AFTER REMOVE:\n" + GdiInterop.DescribeAll());
        Log.Info($"SELFTEST: watchdogFailed={watchdogFailed}");
        Log.Info("==== SELFTEST DONE ====");
    }

    /// <summary>Disruptive (~7s): activate (sole 4K), force the display to 1440p (simulating VD), confirm the session enforcer restores 4K.</summary>
    private static void DiagEnforce()
    {
        Log.Init();
        Log.Info("==== ENFORCE-MODE DIAGNOSTIC (disruptive ~7s) ====");
        var cfg = Config.Load();
        using var vda = new SudoVdaController();
        var session = new VirtualDisplaySession(vda, cfg, () => { });

        if (!session.Activate(out var name) || string.IsNullOrEmpty(name)) { Log.Error("DiagEnforce: activate failed."); session.Deactivate(); return; }
        Log.Info($"DiagEnforce: activated on {name}.");
        Thread.Sleep(1500);

        DisplayManager.SetMode(name!, 2560, 1440, 60); // simulate Virtual Desktop forcing 1440p60
        GdiInterop.TryGetCurrentMode(name!, out var forced);
        Log.Info($"DiagEnforce: after forced override -> {forced.dmPelsWidth}x{forced.dmPelsHeight}@{forced.dmDisplayFrequency}");

        Thread.Sleep(5000); // the session's mode enforcer (every 2s) should correct the drift
        GdiInterop.TryGetCurrentMode(name!, out var fixedMode);
        Log.Info($"DiagEnforce: after enforcer window -> {fixedMode.dmPelsWidth}x{fixedMode.dmPelsHeight}@{fixedMode.dmDisplayFrequency}");

        session.Deactivate();
        Log.Info("==== ENFORCE-MODE DIAGNOSTIC DONE ====");
    }

    /// <summary>Validate restore fidelity: capture, drop a monitor to 60Hz + make it primary, then RestorePhysicalModes back.</summary>
    private static void DiagRestore()
    {
        Log.Init();
        Log.Info("==== RESTORE-FIDELITY DIAGNOSTIC (perturbs a monitor ~1s, then restores) ====");
        var dm = new DisplayManager();
        var snap = dm.Capture(Guid.Empty);
        if (snap is null || snap.Monitors.Count == 0) { Log.Error("DiagRestore: no physical monitors captured."); return; }

        var target = snap.Monitors.FirstOrDefault(m => !m.IsPrimary) ?? snap.Monitors[0];
        Log.Info($"DiagRestore: perturbing {target.GdiName} (refresh {target.RefreshHz}->60, making it primary).");

        DisplayManager.SetMode(target.GdiName, target.Width, target.Height, 60); // simulate refresh drop
        dm.SetPrimaryByGdiName(target.GdiName);                                   // simulate primary change
        Thread.Sleep(700);
        Log.Info("DiagRestore: AFTER PERTURB: " + string.Join(" | ", DisplayManager.EnumPhysicalMonitors()));

        dm.RestorePhysicalModes(snap);                                           // the fix under test
        Thread.Sleep(700);
        Log.Info("DiagRestore: AFTER RESTORE: " + string.Join(" | ", DisplayManager.EnumPhysicalMonitors()));
        Log.Info("==== RESTORE-FIDELITY DIAGNOSTIC DONE ====");
    }

    /// <summary>Safe, non-disruptive: capture the current topology and re-apply it unchanged.</summary>
    private static void SelfTestRestore()
    {
        Log.Init();
        Log.Info("==== TOPOLOGY RESTORE SELFTEST (safe / non-disruptive) ====");
        var dm = new DisplayManager();

        Log.Info("Monitors BEFORE:\n" + GdiInterop.DescribeAll());
        int before = GdiInterop.ActiveMonitorCount();

        var snap = dm.Capture(Guid.Empty);
        if (snap is null) { Log.Error("Capture failed."); return; }
        Log.Info($"Captured {snap.NumPaths} paths / {snap.NumModes} modes. Re-applying unchanged…");

        bool ok = dm.Restore(snap);
        Thread.Sleep(500);
        int after = GdiInterop.ActiveMonitorCount();
        Log.Info($"Restore(unchanged) -> {ok}; active monitors {before} -> {after}.");
        Log.Info("Monitors AFTER:\n" + GdiInterop.DescribeAll());

        // Disk serialize round-trip.
        snap.SaveToDisk();
        var reloaded = DisplaySnapshot.LoadFromDisk();
        bool serialOk = reloaded is not null
            && reloaded.NumPaths == snap.NumPaths
            && reloaded.GetPaths().Length == (int)snap.NumPaths
            && reloaded.GetModes().Length == (int)snap.NumModes;
        DisplaySnapshot.DeleteFromDisk();
        Log.Info($"Disk serialize round-trip -> {serialOk}");
        Log.Info("==== RESTORE SELFTEST DONE ====");
    }

    /// <summary>DISRUPTIVE: full activate (create + primary + disable others) → wait → restore. Monitors go dark briefly.</summary>
    private static void SelfTestFullCycle()
    {
        Log.Init();
        Log.Info("==== FULL-CYCLE SELFTEST (DISRUPTIVE — physical monitors will go dark for ~4s) ====");
        var cfg = Config.Load();
        using var vda = new SudoVdaController();
        bool watchdogFail = false;
        var session = new VirtualDisplaySession(vda, cfg, () => watchdogFail = true);

        Log.Info("Monitors BEFORE:\n" + GdiInterop.DescribeAll());
        if (!session.Activate(out var name)) { Log.Error("Activate failed; aborting."); return; }
        Log.Info($"Activated on {name}. Monitors WHILE ACTIVE:\n" + GdiInterop.DescribeAll());

        Thread.Sleep(4000);

        bool ok = session.Deactivate();
        Thread.Sleep(1000);
        Log.Info($"Deactivate -> {ok}. Monitors AFTER:\n" + GdiInterop.DescribeAll());
        Log.Info($"watchdogFail={watchdogFail}");
        Log.Info("==== FULL-CYCLE SELFTEST DONE ====");
    }
}
