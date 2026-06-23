using System.Runtime.InteropServices;
using VirtualMirage.Interop;
using VirtualMirage.VirtualDisplay;

namespace VirtualMirage.Display;

/// <summary>
/// Layer B: display topology operations. Capture/restore the whole topology via
/// the CCD API; set mode / primary / disable-others via the simpler legacy GDI API
/// (ChangeDisplaySettingsEx) — the same split Apollo uses.
/// </summary>
public sealed class DisplayManager
{
    // ---- Capture ----

    public DisplaySnapshot? Capture(Guid virtualGuid)
    {
        uint extra = CcdInterop.QDC_VIRTUAL_MODE_AWARE | CcdInterop.QDC_VIRTUAL_REFRESH_RATE_AWARE;
        if (!CcdInterop.QueryActive(extra, out var paths, out var modes))
        {
            extra = 0;
            if (!CcdInterop.QueryActive(extra, out paths, out modes))
            {
                Log.Error("Capture: QueryDisplayConfig failed.");
                return null;
            }
        }

        string? primary = FindPrimaryGdiName();
        var active = ListActiveGdiNames();
        var snap = DisplaySnapshot.Build(extra, paths, modes, virtualGuid, primary, active);
        snap.Monitors = EnumPhysicalMonitors();
        Log.Info($"Captured topology: {paths.Length} paths, {modes.Length} modes, primary={primary}, active=[{string.Join(",", active)}].");
        Log.Info($"Captured {snap.Monitors.Count} physical monitor(s): {string.Join(" | ", snap.Monitors)}");
        return snap;
    }

    /// <summary>Active PHYSICAL monitors (adapter string lacking "Virtual") with full DEVMODE + primary flag.</summary>
    public static List<MonitorState> EnumPhysicalMonitors()
    {
        var srcToTarget = CcdInterop.MapActiveSourceToTarget();
        var list = new List<MonitorState>();
        for (uint i = 0; ; i++)
        {
            var dd = new GdiInterop.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<GdiInterop.DISPLAY_DEVICE>() };
            if (!GdiInterop.EnumDisplayDevicesW(null, i, ref dd, 0)) break;

            bool active = (dd.StateFlags & GdiInterop.DISPLAY_DEVICE_ACTIVE) != 0;
            bool isVirtual = dd.DeviceString.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!active || isVirtual) continue;
            if (!GdiInterop.TryGetCurrentMode(dd.DeviceName, out var dm)) continue;

            srcToTarget.TryGetValue(dd.DeviceName, out var tgt);
            list.Add(new MonitorState
            {
                GdiName = dd.DeviceName,
                DevicePath = tgt.devicePath ?? "",
                FriendlyName = tgt.friendly ?? "",
                Width = dm.dmPelsWidth,
                Height = dm.dmPelsHeight,
                RefreshHz = dm.dmDisplayFrequency,
                PosX = dm.dmPositionX,
                PosY = dm.dmPositionY,
                BitsPerPel = dm.dmBitsPerPel,
                IsPrimary = (dd.StateFlags & GdiInterop.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
            });
        }
        return list;
    }

    // ---- Saved layout presets (the user's "non-VR" / "VR" desktop arrangements) ----

    /// <summary>Capture the current desktop topology and persist it to <paramref name="path"/> for later replay.</summary>
    public bool SaveLayoutSnapshot(Guid virtualGuid, string path)
    {
        var snap = Capture(virtualGuid);
        if (snap is null) { Log.Error($"SaveLayoutSnapshot: capture failed; not saving {path}."); return false; }
        snap.SaveToDisk(path);
        return true;
    }

    /// <summary>
    /// Re-apply a previously-saved layout snapshot: the CCD topology (which encodes which monitors are on,
    /// their positions, and any clone/duplicate grouping) plus each captured monitor's exact mode/primary.
    /// Used for the keep-others connect (VR layout) and disconnect (normal layout) paths.
    /// </summary>
    public bool ApplyLayoutSnapshot(string path)
    {
        var snap = DisplaySnapshot.LoadFromDisk(path);
        if (snap is null) { Log.Warn($"ApplyLayoutSnapshot: no saved layout at {path}."); return false; }

        Log.Info($"ApplyLayoutSnapshot: re-applying saved layout from {path} ({snap.NumPaths} paths, {snap.Monitors.Count} monitor(s)).");
        bool ok = RestoreWithRetry(snap, attempts: 3, delayMs: 400);
        Thread.Sleep(200); // let the topology settle before re-asserting per-monitor modes
        RestorePhysicalModes(snap);
        Log.Info($"ApplyLayoutSnapshot: done (ccd ok={ok}). Topology now:\n{GdiInterop.DescribeAll()}");
        return ok;
    }

    public static bool HasSavedLayout(string path) => File.Exists(path);

    /// <summary>
    /// Re-apply a saved "VR layout" for the just-created virtual display <paramref name="vd"/>. The raw CCD
    /// arrays can't be replayed verbatim here because the virtual display's adapter/target IDs change every
    /// session (the snapshot's stale virtual path -> SetDisplayConfig err=87 -> USE_DATABASE_CURRENT, which
    /// never activates the new virtual). So reconstruct from the snapshot's high-level intent with CURRENT
    /// IDs: "virtual only" -> make it the sole display; otherwise keep the saved physical monitors on
    /// alongside the virtual, restore their exact modes, and make the virtual primary. Guarantees the
    /// virtual actually becomes active. (Cross-adapter CLONE/duplicate is not reproduced here.)
    /// </summary>
    public bool ApplyVrLayout(VirtualDisplayHandle vd, string path)
    {
        var snap = DisplaySnapshot.LoadFromDisk(path);
        int physCount = snap?.Monitors?.Count ?? 0;
        Log.Info($"ApplyVrLayout: reconstructing saved VR layout ({physCount} physical monitor(s) + virtual) with current IDs.");

        bool ok;
        if (snap is null || physCount == 0)
        {
            ok = ApplyExclusive(vd); // "virtual only"
        }
        else
        {
            // Activate EXACTLY the saved monitors + the virtual (others off), then restore their modes.
            ok = ActivateVirtualWithMonitors(vd, snap.Monitors);
            Thread.Sleep(200);
            RestorePhysicalModes(snap);
            string? vn = ResolveName(vd);
            if (!string.IsNullOrEmpty(vn) && SavedPrimaryIsVirtual(snap)) SetPrimaryByGdiName(vn);
        }

        string? name = ResolveName(vd);
        bool active = !string.IsNullOrEmpty(name);
        Log.Info($"ApplyVrLayout: done (ok={ok}, virtual={(active ? name : "INACTIVE")}). Topology now:\n{GdiInterop.DescribeAll()}");
        return ok && active;
    }

    /// <summary>True if the saved layout's primary was the virtual display (i.e. not one of its physical monitors).</summary>
    private static bool SavedPrimaryIsVirtual(DisplaySnapshot snap)
        => string.IsNullOrEmpty(snap.PrimaryGdiName)
           || !snap.Monitors.Any(m => string.Equals(m.GdiName, snap.PrimaryGdiName, StringComparison.OrdinalIgnoreCase));

    // ---- Apply (make the virtual display the show) ----

    /// <summary>
    /// Make the virtual display the show. Primary mechanism is the CCD API
    /// (atomic, reliable on Win11 24H2 where legacy ChangeDisplaySettingsEx
    /// silently no-ops); the legacy GDI path is kept only as a fallback.
    /// </summary>
    public bool Apply(VirtualDisplayHandle vd, ResolutionConfig res, bool setPrimary, bool disableOthers)
    {
        bool ok;
        if (disableOthers)
        {
            // Activates the virtual target (works whether it starts active or inactive) AND
            // makes it the sole display => primary, in one atomic CCD call.
            ok = ApplyExclusive(vd);
            if (!ok)
            {
                Log.Warn("ApplyExclusive (CCD) failed; falling back to legacy primary+disable.");
                string? gn = ResolveName(vd);
                if (!string.IsNullOrEmpty(gn)) { if (setPrimary) SetPrimary(gn); ok = DisableAllExcept(gn); }
            }
        }
        else
        {
            // Keep the other monitors on. Reliably turn the (possibly inactive) virtual display ON
            // alongside them, then make it primary if requested. We deliberately DON'T use
            // SDC_TOPOLOGY_EXTEND here: it replays the saved extend-topology, which — because our display
            // has a stable GUID Windows remembers as "off" — left the virtual inactive (so it "only
            // launched with disable-others on") and could also switch a user-disabled monitor back on.
            ok = ActivateVirtualKeepingOthers(vd);
            if (!ok)
            {
                Log.Warn("ActivateVirtualKeepingOthers (CCD) failed; falling back to SDC_TOPOLOGY_EXTEND.");
                ok = CcdInterop.ExtendAllDisplays();
            }
            if (setPrimary)
            {
                string? gn = ResolveName(vd);
                if (!string.IsNullOrEmpty(gn) && !SetPrimaryByGdiName(gn)) SetPrimary(gn); // legacy fallback
            }
        }

        // Now that the display is active, resolve its name and enforce the exact requested mode.
        string? name = ResolveName(vd);
        if (!string.IsNullOrEmpty(name)) SetMode(name, (uint)res.Width, (uint)res.Height, (uint)res.RefreshHz);

        Log.Info($"Apply complete (setPrimary={setPrimary}, disableOthers={disableOthers}, ok={ok}, name={name ?? "?"}). Topology now:\n{GdiInterop.DescribeAll()}");
        return ok;
    }

    /// <summary>Resolve the virtual display's \\.\DISPLAYn name (active paths), waiting briefly for attach.</summary>
    private static string? ResolveName(VirtualDisplayHandle vd)
    {
        for (int i = 0; i < 8; i++)
        {
            var n = CcdInterop.FindGdiNameForTarget(vd.AdapterLuid, vd.TargetId);
            if (!string.IsNullOrEmpty(n)) return n;
            Thread.Sleep(120);
        }
        return string.IsNullOrEmpty(vd.GdiName) ? null : vd.GdiName;
    }

    /// <summary>
    /// CCD: supply a single active path (the virtual target) with OS-chosen modes. Because it's the
    /// only supplied path it becomes the sole display => primary at (0,0). Uses QueryAll so it works
    /// even when the hot-added display came up inactive (Windows remembered it off).
    /// </summary>
    private bool ApplyExclusive(VirtualDisplayHandle vd)
    {
        if (!CcdInterop.QueryAll(out var paths, out _))
        {
            Log.Error("ApplyExclusive: QueryAll failed.");
            return false;
        }

        int vp = FindVirtualPath(paths, vd);
        if (vp < 0) { Log.Error($"ApplyExclusive: virtual path not found among all paths (target {vd.TargetId})."); return false; }

        var vpath = paths[vp];
        vpath.flags |= CcdInterop.DISPLAYCONFIG_PATH_ACTIVE;
        vpath.sourceInfo.modeInfoIdx = CcdInterop.DISPLAYCONFIG_PATH_MODE_IDX_INVALID; // let OS pick the mode
        vpath.targetInfo.modeInfoIdx = CcdInterop.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        var newPaths = new[] { vpath };

        uint baseFlags = CcdInterop.SDC_APPLY | CcdInterop.SDC_USE_SUPPLIED_DISPLAY_CONFIG | CcdInterop.SDC_ALLOW_CHANGES;
        int r = CcdInterop.SetDisplayConfig(1, newPaths, 0, null, baseFlags | CcdInterop.SDC_SAVE_TO_DATABASE);
        if (r != CcdInterop.ERROR_SUCCESS)
        {
            Log.Warn($"ApplyExclusive: apply+save err={r}; retrying without SAVE_TO_DATABASE.");
            r = CcdInterop.SetDisplayConfig(1, newPaths, 0, null, baseFlags);
        }
        Log.Info($"ApplyExclusive: activate-only-virtual (supplied single path) -> {r}.");
        return r == CcdInterop.ERROR_SUCCESS;
    }

    /// <summary>
    /// Turn the virtual display ON alongside the currently-active displays — without disturbing them and
    /// without resurrecting monitors the user had disabled. Supplies the current active config verbatim
    /// plus the virtual target with INVALID mode indices (the OS extends it in), so it works even when the
    /// hot-added display came up inactive — unlike SDC_TOPOLOGY_EXTEND, which replays a stale saved topology.
    /// The virtual lives on its own (SUDOVDA) adapter, so its source can't collide with the physical paths'.
    /// </summary>
    private bool ActivateVirtualKeepingOthers(VirtualDisplayHandle vd)
    {
        // Already on? nothing to turn on.
        if (!string.IsNullOrEmpty(CcdInterop.FindGdiNameForTarget(vd.AdapterLuid, vd.TargetId)))
            return true;

        if (!CcdInterop.QueryActive(0, out var active, out var modes))
        {
            Log.Error("ActivateVirtualKeepingOthers: QueryActive failed.");
            return false;
        }
        if (!CcdInterop.QueryAll(out var all, out _))
        {
            Log.Error("ActivateVirtualKeepingOthers: QueryAll failed.");
            return false;
        }

        int vp = FindVirtualPath(all, vd);
        if (vp < 0) { Log.Error($"ActivateVirtualKeepingOthers: virtual path not found (target {vd.TargetId})."); return false; }

        var vpath = all[vp];
        vpath.flags |= CcdInterop.DISPLAYCONFIG_PATH_ACTIVE;
        vpath.sourceInfo.modeInfoIdx = CcdInterop.DISPLAYCONFIG_PATH_MODE_IDX_INVALID; // OS computes the virtual's mode + position
        vpath.targetInfo.modeInfoIdx = CcdInterop.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;

        // Supplied config = the existing active paths verbatim (modes preserved) + the virtual (extend it in).
        var paths = new CcdInterop.DISPLAYCONFIG_PATH_INFO[active.Length + 1];
        Array.Copy(active, paths, active.Length);
        paths[active.Length] = vpath;

        uint flags = CcdInterop.SDC_APPLY | CcdInterop.SDC_USE_SUPPLIED_DISPLAY_CONFIG | CcdInterop.SDC_ALLOW_CHANGES;
        int r = CcdInterop.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags | CcdInterop.SDC_SAVE_TO_DATABASE);
        if (r != CcdInterop.ERROR_SUCCESS)
        {
            Log.Warn($"ActivateVirtualKeepingOthers: apply+save err={r}; retrying without SAVE_TO_DATABASE.");
            r = CcdInterop.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags);
        }
        Log.Info($"ActivateVirtualKeepingOthers: virtual + {active.Length} active path(s), extend -> {r}.");
        return r == CcdInterop.ERROR_SUCCESS;
    }

    /// <summary>
    /// Activate EXACTLY the virtual display + the given physical monitors (matched by stable device path),
    /// turning OFF every other physical monitor. Like ApplyExclusive but keeping a chosen set alongside the
    /// virtual — this is what makes a saved VR layout come back as "just monitors 3 + 4" instead of all of
    /// them. Supplies only those targets with INVALID mode indices; everything not supplied drops to off.
    /// </summary>
    private bool ActivateVirtualWithMonitors(VirtualDisplayHandle vd, List<MonitorState> keep)
    {
        if (!CcdInterop.QueryAll(out var all, out _)) { Log.Error("ActivateVirtualWithMonitors: QueryAll failed."); return false; }

        var keepPaths = new HashSet<string>(
            keep.Select(m => m.DevicePath).Where(s => !string.IsNullOrEmpty(s)), StringComparer.OrdinalIgnoreCase);

        var supplied = new List<CcdInterop.DISPLAYCONFIG_PATH_INFO>();
        int vp = FindVirtualPath(all, vd);
        if (vp >= 0) supplied.Add(MakeActiveInvalid(all[vp]));
        else Log.Warn($"ActivateVirtualWithMonitors: virtual path not found (target {vd.TargetId}).");

        int matched = 0;
        var addedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < all.Length && keepPaths.Count > 0; i++)
        {
            if (i == vp) continue;
            var (devPath, _) = CcdInterop.GetTargetDeviceName(all[i].targetInfo.adapterId, all[i].targetInfo.id);
            if (string.IsNullOrEmpty(devPath) || !keepPaths.Contains(devPath) || !addedDevices.Add(devPath)) continue;
            supplied.Add(MakeActiveInvalid(all[i]));
            matched++;
        }

        if (supplied.Count == 0) { Log.Error("ActivateVirtualWithMonitors: nothing to activate."); return false; }
        if (matched < keepPaths.Count)
            Log.Warn($"ActivateVirtualWithMonitors: only matched {matched}/{keepPaths.Count} saved monitor(s) by device path.");

        var paths = supplied.ToArray();
        uint flags = CcdInterop.SDC_APPLY | CcdInterop.SDC_USE_SUPPLIED_DISPLAY_CONFIG | CcdInterop.SDC_ALLOW_CHANGES;
        int r = CcdInterop.SetDisplayConfig((uint)paths.Length, paths, 0, null, flags | CcdInterop.SDC_SAVE_TO_DATABASE);
        if (r != CcdInterop.ERROR_SUCCESS)
        {
            Log.Warn($"ActivateVirtualWithMonitors: apply+save err={r}; retrying without SAVE_TO_DATABASE.");
            r = CcdInterop.SetDisplayConfig((uint)paths.Length, paths, 0, null, flags);
        }
        Log.Info($"ActivateVirtualWithMonitors: virtual + {matched} saved monitor(s) active, others off -> {r}.");
        return r == CcdInterop.ERROR_SUCCESS;
    }

    private static CcdInterop.DISPLAYCONFIG_PATH_INFO MakeActiveInvalid(CcdInterop.DISPLAYCONFIG_PATH_INFO p)
    {
        p.flags |= CcdInterop.DISPLAYCONFIG_PATH_ACTIVE;
        p.sourceInfo.modeInfoIdx = CcdInterop.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        p.targetInfo.modeInfoIdx = CcdInterop.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        return p;
    }

    private static int FindVirtualPath(CcdInterop.DISPLAYCONFIG_PATH_INFO[] paths, VirtualDisplayHandle vd)
    {
        for (int i = 0; i < paths.Length; i++)
            if (paths[i].targetInfo.adapterId.SameAs(vd.AdapterLuid) && paths[i].targetInfo.id == vd.TargetId)
                return i;

        // fallback: match by source GDI name
        if (!string.IsNullOrEmpty(vd.GdiName))
            for (int i = 0; i < paths.Length; i++)
            {
                var name = CcdInterop.GetSourceGdiName(paths[i].sourceInfo.adapterId, paths[i].sourceInfo.id);
                if (string.Equals(name, vd.GdiName, StringComparison.OrdinalIgnoreCase)) return i;
            }
        return -1;
    }

    // ---- Mode enforcement (counters Virtual Desktop overriding the resolution after connect) ----

    /// <summary>
    /// If the virtual display drifted from the requested mode (e.g. Virtual Desktop applied its own
    /// stream resolution after connecting), force it back. No-op (no flicker) when already correct.
    /// </summary>
    public bool EnforceMode(VirtualDisplayHandle vd, ResolutionConfig res)
    {
        string? name = CcdInterop.FindGdiNameForTarget(vd.AdapterLuid, vd.TargetId);
        if (string.IsNullOrEmpty(name)) return false; // not active

        if (GdiInterop.TryGetCurrentMode(name, out var cur)
            && cur.dmPelsWidth == (uint)res.Width && cur.dmPelsHeight == (uint)res.Height
            && cur.dmDisplayFrequency == (uint)res.RefreshHz)
            return true; // already correct — do nothing

        Log.Info($"Mode drift on {name}: now {cur.dmPelsWidth}x{cur.dmPelsHeight}@{cur.dmDisplayFrequency}; forcing {res}.");

        // Try legacy first (cheap); fall back to the CCD path (reliable on 24H2).
        SetMode(name, (uint)res.Width, (uint)res.Height, (uint)res.RefreshHz);
        if (GdiInterop.TryGetCurrentMode(name, out var after)
            && after.dmPelsWidth == (uint)res.Width && after.dmPelsHeight == (uint)res.Height
            && after.dmDisplayFrequency == (uint)res.RefreshHz)
            return true;

        return SetModeViaCcd(vd, res);
    }

    private bool SetModeViaCcd(VirtualDisplayHandle vd, ResolutionConfig res)
    {
        if (!CcdInterop.QueryActive(0, out var paths, out var modes)) return false;
        int vp = FindVirtualPath(paths, vd);
        if (vp < 0) return false;

        uint sIdx = paths[vp].sourceInfo.modeInfoIdx;
        if (sIdx == CcdInterop.DISPLAYCONFIG_PATH_MODE_IDX_INVALID || sIdx >= modes.Length) return false;

        modes[sIdx].mode.sourceMode.width = (uint)res.Width;
        modes[sIdx].mode.sourceMode.height = (uint)res.Height;
        paths[vp].targetInfo.modeInfoIdx = CcdInterop.DISPLAYCONFIG_PATH_MODE_IDX_INVALID; // OS recomputes timing
        paths[vp].targetInfo.refreshRate = new CcdInterop.DISPLAYCONFIG_RATIONAL { Numerator = (uint)res.RefreshHz, Denominator = 1 };
        paths[vp].targetInfo.scanLineOrdering = 1; // progressive

        uint flags = CcdInterop.SDC_APPLY | CcdInterop.SDC_USE_SUPPLIED_DISPLAY_CONFIG
                   | CcdInterop.SDC_ALLOW_CHANGES | CcdInterop.SDC_SAVE_TO_DATABASE;
        int r = CcdInterop.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags);
        Log.Info($"SetModeViaCcd {res} -> {r}.");
        return r == CcdInterop.ERROR_SUCCESS;
    }

    /// <summary>Make <paramref name="gdiName"/> primary via CCD using a FRESH active query (avoids the stale-array err=87).</summary>
    public bool SetPrimaryByGdiName(string gdiName)
    {
        if (string.IsNullOrEmpty(gdiName)) return false;
        if (!CcdInterop.QueryActive(0, out var paths, out var modes)) return false;

        int vp = -1;
        for (int i = 0; i < paths.Length; i++)
        {
            var n = CcdInterop.GetSourceGdiName(paths[i].sourceInfo.adapterId, paths[i].sourceInfo.id);
            if (string.Equals(n, gdiName, StringComparison.OrdinalIgnoreCase)) { vp = i; break; }
        }
        if (vp < 0) { Log.Warn($"SetPrimaryByGdiName: {gdiName} not active."); return false; }

        uint sIdx = paths[vp].sourceInfo.modeInfoIdx;
        if (sIdx == CcdInterop.DISPLAYCONFIG_PATH_MODE_IDX_INVALID || sIdx >= modes.Length) return false;
        int offX = modes[sIdx].mode.sourceMode.position.x;
        int offY = modes[sIdx].mode.sourceMode.position.y;
        if (offX == 0 && offY == 0) { Log.Info($"SetPrimaryByGdiName: {gdiName} already primary."); return true; }

        for (int i = 0; i < modes.Length; i++)
        {
            if (modes[i].infoType != CcdInterop.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) continue;
            modes[i].mode.sourceMode.position.x -= offX;
            modes[i].mode.sourceMode.position.y -= offY;
        }
        uint flags = CcdInterop.SDC_APPLY | CcdInterop.SDC_USE_SUPPLIED_DISPLAY_CONFIG
                   | CcdInterop.SDC_ALLOW_CHANGES | CcdInterop.SDC_SAVE_TO_DATABASE;
        int r = CcdInterop.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags);
        Log.Info($"SetPrimaryByGdiName {gdiName} -> {r}.");
        return r == CcdInterop.ERROR_SUCCESS;
    }

    /// <summary>
    /// Faithfully restore each captured physical monitor's resolution/refresh/position via the legacy
    /// API (mode changes work on 24H2) and re-assert the captured primary via CCD. Runs AFTER the CCD
    /// topology restore re-activated the monitors — this is what fixes "360Hz drops to 120 / primary moves".
    /// </summary>
    public void RestorePhysicalModes(DisplaySnapshot snap)
    {
        if (snap.Monitors is not { Count: > 0 }) { Log.Info("RestorePhysicalModes: no captured monitor states; skipping."); return; }

        int applied = 0;
        foreach (var ms in snap.Monitors)
        {
            string gdi = ms.GdiName;
            if (!GdiInterop.TryGetCurrentMode(gdi, out _))
            {
                var remap = CcdInterop.FindSourceForDevicePath(ms.DevicePath); // gdiName changed (e.g. across reboot)
                if (string.IsNullOrEmpty(remap) || !GdiInterop.TryGetCurrentMode(remap, out _))
                {
                    Log.Warn($"RestorePhysicalModes: {ms} not currently active; skipping.");
                    continue;
                }
                gdi = remap!;
            }

            GdiInterop.TryGetCurrentMode(gdi, out var dm); // seed private driver fields
            dm.dmPelsWidth = ms.Width;
            dm.dmPelsHeight = ms.Height;
            dm.dmDisplayFrequency = ms.RefreshHz;
            dm.dmPositionX = ms.PosX;
            dm.dmPositionY = ms.PosY;
            if (ms.BitsPerPel != 0) dm.dmBitsPerPel = ms.BitsPerPel;
            dm.dmFields = GdiInterop.DM_PELSWIDTH | GdiInterop.DM_PELSHEIGHT | GdiInterop.DM_DISPLAYFREQUENCY
                        | GdiInterop.DM_POSITION | GdiInterop.DM_BITSPERPEL;

            int r = GdiInterop.ChangeDisplaySettingsExW(gdi, ref dm, IntPtr.Zero,
                GdiInterop.CDS_UPDATEREGISTRY | GdiInterop.CDS_NORESET, IntPtr.Zero);
            Log.Info($"RestorePhysicalModes: {gdi} -> {ms.Width}x{ms.Height}@{ms.RefreshHz} @({ms.PosX},{ms.PosY}) : {r}");
            applied++;
        }

        int commit = GdiInterop.ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Log.Info($"RestorePhysicalModes: commit -> {commit} ({applied} monitor(s)).");

        var primary = snap.Monitors.FirstOrDefault(m => m.IsPrimary);
        if (primary is not null) SetPrimaryByGdiName(primary.GdiName);
    }

    public static bool SetMode(string gdi, uint w, uint h, uint hz)
    {
        if (!GdiInterop.TryGetCurrentMode(gdi, out var dm))
        {
            Log.Warn($"SetMode: cannot read current mode of {gdi}; using a fresh DEVMODE.");
            dm = GdiInterop.NewDevMode();
        }
        if (dm.dmPelsWidth == w && dm.dmPelsHeight == h && dm.dmDisplayFrequency == hz)
        {
            Log.Info($"SetMode: {gdi} already {w}x{h}@{hz}.");
            return true;
        }

        dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmDisplayFrequency = hz;
        dm.dmFields = GdiInterop.DM_PELSWIDTH | GdiInterop.DM_PELSHEIGHT | GdiInterop.DM_DISPLAYFREQUENCY;

        int r = GdiInterop.ChangeDisplaySettingsExW(gdi, ref dm, IntPtr.Zero, GdiInterop.CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (r != GdiInterop.DISP_CHANGE_SUCCESSFUL && hz > 1)
        {
            foreach (uint alt in new[] { hz - 1, hz + 1 }) // ±1 Hz fallback (Apollo pattern)
            {
                dm.dmDisplayFrequency = alt;
                r = GdiInterop.ChangeDisplaySettingsExW(gdi, ref dm, IntPtr.Zero, GdiInterop.CDS_UPDATEREGISTRY, IntPtr.Zero);
                if (r == GdiInterop.DISP_CHANGE_SUCCESSFUL) { Log.Info($"SetMode: {gdi} accepted {w}x{h}@{alt} (±1 fallback)."); break; }
            }
        }
        Log.Info($"SetMode {gdi} -> {w}x{h}@{hz} : {r}");
        return r == GdiInterop.DISP_CHANGE_SUCCESSFUL;
    }

    /// <summary>Make <paramref name="gdi"/> the primary display (legacy CDS_SET_PRIMARY; Apollo's approach).</summary>
    public static bool SetPrimary(string gdi)
    {
        if (!GdiInterop.TryGetCurrentMode(gdi, out var target))
        {
            Log.Error($"SetPrimary: cannot read {gdi}.");
            return false;
        }
        int offX = target.dmPositionX, offY = target.dmPositionY;

        ForEachActiveDevice(name =>
        {
            if (!GdiInterop.TryGetCurrentMode(name, out var dm)) return;
            dm.dmPositionX -= offX;
            dm.dmPositionY -= offY;
            dm.dmFields = GdiInterop.DM_POSITION;
            uint flags = GdiInterop.CDS_UPDATEREGISTRY | GdiInterop.CDS_NORESET;
            if (string.Equals(name, gdi, StringComparison.OrdinalIgnoreCase)) flags |= GdiInterop.CDS_SET_PRIMARY;
            GdiInterop.ChangeDisplaySettingsExW(name, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
        });

        int r = GdiInterop.ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); // commit
        Log.Info($"SetPrimary {gdi} commit -> {r}");
        return r == GdiInterop.DISP_CHANGE_SUCCESSFUL;
    }

    /// <summary>Detach every active display except <paramref name="keepGdi"/> (zero-size DEVMODE trick).</summary>
    public static bool DisableAllExcept(string keepGdi)
    {
        var toDisable = new List<string>();
        ForEachActiveDevice(name =>
        {
            if (!string.Equals(name, keepGdi, StringComparison.OrdinalIgnoreCase)) toDisable.Add(name);
        });

        if (toDisable.Count == 0) { Log.Info($"DisableAllExcept({keepGdi}): nothing else active."); return true; }

        foreach (var name in toDisable)
        {
            var dm = GdiInterop.NewDevMode();
            dm.dmFields = GdiInterop.DM_POSITION; // per MS docs: DM_POSITION + zero width/height detaches
            dm.dmPositionX = 0; dm.dmPositionY = 0;
            dm.dmPelsWidth = 0; dm.dmPelsHeight = 0;
            int r = GdiInterop.ChangeDisplaySettingsExW(name, ref dm, IntPtr.Zero,
                GdiInterop.CDS_UPDATEREGISTRY | GdiInterop.CDS_NORESET, IntPtr.Zero);
            Log.Info($"Disable {name} -> {r}");
        }
        int c = GdiInterop.ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); // commit
        Log.Info($"DisableAllExcept({keepGdi}) commit -> {c} (disabled {toDisable.Count}).");
        return c == GdiInterop.DISP_CHANGE_SUCCESSFUL;
    }

    // ---- Restore ----

    public bool RestoreWithRetry(DisplaySnapshot snap, int attempts = 6, int delayMs = 800)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (Restore(snap)) return true;
            Thread.Sleep(delayMs);
        }
        Log.Error("RestoreWithRetry: exhausted attempts.");
        return false;
    }

    public bool Restore(DisplaySnapshot snap)
    {
        var paths = snap.GetPaths();
        var modes = snap.GetModes();

        uint flags = CcdInterop.SDC_APPLY | CcdInterop.SDC_USE_SUPPLIED_DISPLAY_CONFIG
                   | CcdInterop.SDC_ALLOW_CHANGES | CcdInterop.SDC_SAVE_TO_DATABASE;
        if ((snap.QueryExtraFlags & CcdInterop.QDC_VIRTUAL_MODE_AWARE) != 0) flags |= CcdInterop.SDC_VIRTUAL_MODE_AWARE;
        if ((snap.QueryExtraFlags & CcdInterop.QDC_VIRTUAL_REFRESH_RATE_AWARE) != 0) flags |= CcdInterop.SDC_VIRTUAL_REFRESH_RATE_AWARE;

        int r = CcdInterop.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags);
        if (r == CcdInterop.ERROR_SUCCESS) { Log.Info("Restore: re-applied saved topology."); return true; }
        Log.Warn($"Restore: supplied-config apply err={r}; trying USE_DATABASE_CURRENT.");

        r = CcdInterop.SetDisplayConfig(0, null, 0, null, CcdInterop.SDC_APPLY | CcdInterop.SDC_USE_DATABASE_CURRENT);
        if (r == CcdInterop.ERROR_SUCCESS) { Log.Info("Restore: applied USE_DATABASE_CURRENT."); return true; }
        Log.Warn($"Restore: USE_DATABASE_CURRENT err={r}; trying TOPOLOGY_EXTEND (turn everything on).");

        r = CcdInterop.SetDisplayConfig(0, null, 0, null, CcdInterop.SDC_APPLY | CcdInterop.SDC_TOPOLOGY_EXTEND);
        Log.Info($"Restore: TOPOLOGY_EXTEND -> {r}");
        return r == CcdInterop.ERROR_SUCCESS;
    }

    // ---- enumeration helpers ----

    public static string? FindPrimaryGdiName()
    {
        string? primary = null;
        ForEachDevice((name, flags) =>
        {
            if ((flags & GdiInterop.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0) primary = name;
        });
        return primary;
    }

    public static List<string> ListActiveGdiNames()
    {
        var list = new List<string>();
        ForEachActiveDevice(list.Add);
        return list;
    }

    /// <summary>
    /// Detect duplicated (cloned) monitors. In the CCD API a duplicate shows up as two+ ACTIVE paths that
    /// share one source (same source adapterId+id) and/or the same cloneGroupId (the low bits of the
    /// source modeInfoIdx union when queried virtual-mode-aware). Extended monitors each get their own
    /// source. Returns a human-readable dump (also used to learn how a cross-adapter virtual↔GPU clone
    /// is represented).
    /// </summary>
    public static string DescribeCloneGroups()
    {
        if (!CcdInterop.QueryActive(CcdInterop.QDC_VIRTUAL_MODE_AWARE, out var paths, out _)
            && !CcdInterop.QueryActive(0, out paths, out _))
            return "  (QueryDisplayConfig failed)";

        var perSource = new Dictionary<long, int>();
        foreach (var p in paths)
        {
            if ((p.flags & CcdInterop.DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
            long sk = p.sourceInfo.adapterId.ToInt64() * 1000003 + p.sourceInfo.id;
            perSource[sk] = perSource.TryGetValue(sk, out var c) ? c + 1 : 1;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(perSource.Values.Any(v => v > 1)
            ? "DUPLICATE/CLONE DETECTED — a source is driving 2+ monitors:"
            : "No duplicate detected — each active monitor has its own source:");
        foreach (var p in paths)
        {
            if ((p.flags & CcdInterop.DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
            string src = CcdInterop.GetSourceGdiName(p.sourceInfo.adapterId, p.sourceInfo.id) ?? "?";
            string tgt = CcdInterop.GetTargetFriendlyName(p.targetInfo.adapterId, p.targetInfo.id) ?? "?";
            long sk = p.sourceInfo.adapterId.ToInt64() * 1000003 + p.sourceInfo.id;
            bool shared = perSource[sk] > 1;
            sb.AppendLine($"  {src} [srcAdp {p.sourceInfo.adapterId.ToInt64():X}/{p.sourceInfo.id}] -> {tgt} " +
                          $"[tgtAdp {p.targetInfo.adapterId.ToInt64():X}/{p.targetInfo.id}] srcModeIdx=0x{p.sourceInfo.modeInfoIdx:X8}" +
                          (shared ? "   <== shares source (CLONE)" : ""));
        }
        return sb.ToString();
    }

    private static void ForEachActiveDevice(Action<string> action)
        => ForEachDevice((name, flags) => { if ((flags & GdiInterop.DISPLAY_DEVICE_ACTIVE) != 0) action(name); });

    private static void ForEachDevice(Action<string, uint> action)
    {
        for (uint i = 0; ; i++)
        {
            var dd = new GdiInterop.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<GdiInterop.DISPLAY_DEVICE>() };
            if (!GdiInterop.EnumDisplayDevicesW(null, i, ref dd, 0)) break;
            action(dd.DeviceName, dd.StateFlags);
        }
    }
}
