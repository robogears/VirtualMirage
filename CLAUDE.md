# CLAUDE.md — AutoVRVD

Guidance for working in this repo. Read this first.

## What this is

**AutoVRVD** is a Windows **system-tray app** (C# / .NET 8 / WinForms) that auto-creates a
**4K (3840×2160) @ 120 Hz virtual display** when the user connects to the PC in VR through
**Virtual Desktop**, makes it the sole primary display, disables the physical monitors, and
**restores everything exactly** on disconnect. It lets the user play flat games at 4K120 on a
headless virtual screen viewed in VR.

It does for *Virtual Desktop* what Apollo/Sunshine do for a Moonlight stream, and it reuses the
**SUDOVDA** virtual-display driver (already installed on the dev machine via Apollo, at
`C:\Program Files\Apollo\drivers\sudovda\`). The architecture deliberately mirrors Apollo's
two-layer design + `libdisplaydevice`.

End-user docs live in [README.md](README.md). This file is for developers/agents.

## Build & run

**The .NET 8 SDK is installed *user-local* and is NOT on the system PATH** (the machine only has
.NET runtimes on PATH). Always build with the helper or the explicit SDK path:

```powershell
.\build.ps1                       # Release build (auto-locates the SDK)
.\build.ps1 -Publish              # framework-dependent publish -> .\publish
.\build.ps1 -Run                  # build + launch
# or directly:
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src\AutoVRVD\AutoVRVD.csproj -c Release
```

- Target: `net8.0-windows`, x64, `WinExe` (no console), `AllowUnsafeBlocks`, nullable + implicit usings on.
- **No external NuGet dependencies** — custom logger, `System.Text.Json`, and P/Invoke only. Keep it that way (hermetic build).
- Output: `src\AutoVRVD\bin\<Config>\net8.0-windows\AutoVRVD.exe`.

## Testing (do this instead of clicking the tray)

There is **no headset in the dev environment**, so most behavior is validated through headless
CLI self-test modes that log to `%AppData%\AutoVRVD\logs`. Prefer these; read the log to verify.
**CLI flags are dispatched in `Program.Main` BEFORE the tray/session/orchestrator are constructed**;
each one exits immediately after running (the tray never launches — this is by design for headless
testing).

```powershell
AutoVRVD.exe --selftest-vda          # SAFE: create 4K120 display, confirm, remove (no topology change)
AutoVRVD.exe --selftest-restore      # SAFE: snapshot topology + re-apply unchanged (validates CCD)
AutoVRVD.exe --selftest-fullcycle    # DISRUPTIVE: full activate -> 4s -> restore (monitors go DARK)
AutoVRVD.exe --diag-enforce          # DISRUPTIVE (~7s): activate sole 4K, force 1440p to simulate VD,
                                     #   confirm the session mode-enforcer restores 4K
AutoVRVD.exe --diag-restore          # MILD: perturb a monitor (60Hz + primary), confirm RestorePhysicalModes
                                     #   snaps refresh+primary back (needs physical monitors active, i.e. NOT in VR)
AutoVRVD.exe --diagnose [seconds]    # detection calibration capture (needs the user's headset)
AutoVRVD.exe --selftest-settings     # construct the settings form (catches layout exceptions)
AutoVRVD.exe --selftest-update       # run an update-check and log the result (no download/apply)
```

⚠️ **`--selftest-fullcycle` and `--diag-enforce` black out / reconfigure all physical monitors for
several seconds.** They auto-restore and have safety nets, but **ask the user before running them**
while they're at the machine. The other selftests are non-disruptive.

Logs are the primary debugging tool — log liberally via `Log.Info/Warn/Error`. `GdiInterop.DescribeAll()`
dumps every display + mode and is the quickest way to see topology state in the log.

### Reading the log — device EDID identities

Multiple virtual monitors can coexist, so identify them by EDID name in the log:

- **Ours (SUDOVDA):** monitor EDID name **`Legacy Moonli`**, hardware-id prefix **`SMKD1CE`**. The
  adapter string is `SudoMaker Virtual Display Adapter` but the **monitor** shows as `Legacy Moonli`.
- **Virtual Desktop's OWN native virtual monitor:** **`Linux FHD`** (`LNX0000`). This is a *separate*
  virtual-display system that VD can add itself — see the topology-race section below.
- **Meta's:** `Meta Virtual Monitor`.

## Architecture / code map

Flow: **detector → orchestrator → session → (SUDOVDA + display topology)**.

```
src/AutoVRVD/
  Program.cs                 Entry point. CLI selftest/diag branches (BEFORE the tray), single-instance
                             mutex (Local\AutoVRVD_SingleInstance), DPI/visual-styles, builds & wires
                             tray + session + orchestrator + UpdateController. Calls RecoverIfNeeded()
                             BEFORE the detector starts. Manual tray activate/deactivate run on Task.Run.
  Config.cs                  Config/ResolutionConfig/DetectionConfig; JSON at %AppData%\AutoVRVD\config.json.
                             EnsureDefaults() mints a stable MonitorGuid on first run. UpdateCheckOnLaunch
                             (bool, default true).
  Paths.cs                   %AppData%\AutoVRVD paths (AppDir, LogsDir, ConfigPath, StatePath).
  Logging.cs                 `Log` static (file + in-memory ring; daily rotation). No NuGet.
  Autostart.cs               HKCU\...\Run toggle (no admin): IsEnabled()/Set(enabled).
  Orchestrator.cs            State machine: detector events -> session Activate/Deactivate, gated by
                             AutomationEnabled + the Apollo-contention guard (SkipIfApolloActive ->
                             GdiInterop.HasActiveDisplayMatching("SudoMaker")). Updates tray status.
  VirtualDisplaySession.cs   The worker: Activate (Capture -> CreateDisplay -> persist state -> Apply ->
                             StartModeEnforcer), Deactivate (StopModeEnforcer -> Restore -> RemoveDisplay
                             -> delete state), RecoverIfNeeded. Hosts the per-session mode enforcer.

  Interop/Native.cs          LUID (LowPart/HighPart, ToInt64/FromInt64/SameAs), POINTL, RECT,
                             CreateFileW, DeviceIoControl.

  VirtualDisplay/  (Layer A — the SUDOVDA driver)
    SudoVdaInterop.cs        Interface GUID, IOCTL codes (CTL_CODE), structs, SetupAPI OpenDevice(),
                             by-value IOCTL helpers: IoctlInOut<TIn,TOut>(), IoctlIn<TIn>(),
                             IoctlOut<TOut>(), IoctlNone(). Mirrors sudovda-ioctl.h.
    SudoVdaController.cs      Open/CreateDisplay/RemoveDisplay/SetRenderAdapter/Ping +
                             TryGetProtocolVersion/TryGetWatchdog + watchdog start. Reentrant _gate.
    VirtualDisplayHandle      Record struct identifying a created display
                             (GdiName, AdapterLuid, TargetId, MonitorGuid). Defined in SudoVdaController.cs.
    WatchdogPinger.cs        Background ping thread; interval = (watchdog_timeout_seconds*1000)/3 ms,
                             clamped to a 250ms minimum via Math.Max(250, ...). Fires onFail after misses.

  Display/  (Layer B — desktop topology)
    CcdInterop.cs            CCD API (QueryDisplayConfig/SetDisplayConfig/DisplayConfigGetDeviceInfo)
                             + all DISPLAYCONFIG_* structs (blittable; BOOL as uint). Query/QueryActive/
                             QueryAll, ExtendAllDisplays, name resolution (FindGdiNameForTarget/
                             GetSourceGdiName/GetTargetFriendlyName). Constants
                             DISPLAYCONFIG_PATH_MODE_IDX_INVALID=0xffffffff, QDC_ALL_PATHS=0x1,
                             QDC_ONLY_ACTIVE_PATHS=0x2.
    GdiInterop.cs            Legacy GDI: DEVMODE, EnumDisplayDevices/Settings, ChangeDisplaySettingsEx,
                             TryGetCurrentMode, ActiveMonitorCount, DescribeAll (diagnostics),
                             HasActiveDisplayMatching (contention guard).
    DisplayManager.cs        Capture (-> DisplaySnapshot? incl. per-physical-monitor EnumPhysicalMonitors()),
                             Apply (ApplyExclusive / ApplyPrimaryKeepOthers + legacy fallback), EnforceMode
                             (SetMode then SetModeViaCcd), RestorePhysicalModes (legacy per-monitor mode +
                             SetPrimaryByGdiName via fresh CCD query), ResolveName, RestoreWithRetry ->
                             Restore (3 fallback levels), SetMode/SetPrimary/DisableAllExcept (legacy statics).
    DisplaySnapshot.cs       Serializable topology (CCD path/mode arrays as base64 via MemoryMarshal) PLUS a
                             per-physical-monitor MonitorState list (res/refresh/pos/primary/devicePath) for
                             faithful revert + crash recovery. SaveToDisk/LoadFromDisk/DeleteFromDisk.

  Detection/
    VdSignals.cs             IsStreamerRunning ("VirtualDesktop.Streamer"), MmfExists, TryEventPulse
                             (BodyStateEvent), GetVdLanPeers/HasLanPeer (VD-port LAN peer), IsPrivate
                             (loopback + cloud-relay filter).
    VdSessionDetector.cs     Debounced poller (PollIntervalMs, default 500). EvaluateRaw() checks
                             streamer running + signal mode (ports/event/auto) with private-IP filter ->
                             Connected/Disconnected events. Reads Config live each poll.
    DetectionDiagnostics.cs  Calibration: Run() logs every signal @250ms to a report file; marks
                             transitions with ">>> raw active changed".

  UI/
    TrayApp.cs               NotifyIcon + context menu; SetStatus/Notify/SetUpdateMenu + events;
                             marshals to UI thread via a hidden control.
    StatusIcons.cs           AppStatus enum (Idle/Disabled/Connected/Active/Error) + runtime-generated
                             colored-dot icons (StatusIcons.For).
    SettingsForm.cs          Settings dialog over Config (+ Autostart): resolution, refresh, detection
                             mode/debounce, startup, "Check for updates on launch".

  Update/  (in-app self-updater)
    Updater.cs               GitHub releases/latest poll, numeric-semver compare, asset match on
                             "win-x64.exe", stream download to %TEMP%, detached .cmd swap-and-relaunch.
                             Owner/Repo/AssetSubstring are hardcoded consts.
    UpdateController.cs       Tray-menu state machine (Idle->Checking->Available->Downloading->Ready->
                             Restarting); StartLaunchCheckAsync(). Private _gate.
```

Data/runtime files (all under `%AppData%\AutoVRVD\`): `config.json`, `logs\autovrvd-*.log`,
`logs\diagnostics-*.log`, `session.state.json` (present only while a session is active / after a crash).

## Critical constraints & gotchas (read before changing display or driver code)

1. **Win11 24H2 (build 26200) breaks legacy display *topology* reconfig — but NOT mode changes.**
   `ChangeDisplaySettingsEx` with `CDS_SET_PRIMARY` / detach **silently no-ops** (returns success,
   changes nothing). **For topology, use the CCD `SetDisplayConfig` API.** The proven path is
   `DisplayManager.ApplyExclusive` (supply a single active path → the virtual display becomes the
   sole primary). Legacy `SetPrimary`/`DisableAllExcept` exist only as a fallback for other Windows
   builds.
   **However, legacy mode CHANGES (resolution/refresh via `ChangeDisplaySettingsEx`) DO work on
   24H2** — this is what `DisplayManager.SetMode` / the mode enforcer rely on. Only the *topology*
   ops (set-primary / detach) are broken. Validated via `--diag-enforce`.

2. **A hot-added SUDOVDA display comes up INACTIVE on repeat adds.** Because we use a *stable*
   `MonitorGuid`, Windows remembers the monitor's last state; after the first restore it's remembered
   as "off", so the next `CreateDisplay` attaches it inactive. Therefore: **find it with
   `CcdInterop.QueryAll` (QDC_ALL_PATHS), not active-paths-only, and activate it with INVALID mode
   indices** (`DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xffffffff` on both source + target modeInfoIdx,
   OS picks the mode), which `ApplyExclusive` does. `CreateDisplay` returning an empty `GdiName` is
   EXPECTED and not an error — `CreateDisplay` retries name resolution best-effort (4 short tries,
   ~20–160ms) but may still return empty; `DisplayManager.Apply` then activates and resolves it via
   `ResolveName` (polls `FindGdiNameForTarget` 8x @120ms, falling back to `vd.GdiName`).

3. **Keep CCD structs blittable.** Model Win32 `BOOL` as `uint`, never `bool` (enforced by a comment
   at the top of `CcdInterop`). The path/mode arrays are serialized (base64) and pointer-marshaled; a
   `bool` field breaks both. `DisplaySnapshot` round-trips them with `MemoryMarshal.AsBytes` /
   `MemoryMarshal.Cast<byte, DISPLAYCONFIG_*>` over the raw bytes.

4. **SUDOVDA interop layout is exact** (`SudoVdaInterop`): `ADD_PARAMS` is `Pack=4` with
   `fixed byte[14]` DeviceName/SerialNumber and **integer-Hz** `RefreshRate`. These two fields are
   **ASCII-only** (zero-padded + NUL-terminated by `WriteFixed()` via `Encoding.ASCII`; UTF-8 is NOT
   supported). `PROTOCOL_VERSION` is `Pack=1` (C++ `bool` → C# `byte`). SetupAPI detail-buffer header
   `cbSize` = 8 on x64 / 6 on x86. IOCTLs = `CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, fn, 0, 0)`.
   Interface GUID `{e5bcc234-1e0c-418a-a0d4-ef8b7501414d}`, hardware id `root\sudomaker\sudovda`.
   Installed driver protocol is **0.2.1**; at runtime the code logs the version and **warns only on a
   `Major` mismatch** vs `ExpectedMajor` (`ExpectedMinor` is defined but not enforced; `Incremental` /
   `TestBuild` aren't gated). Verify with `--selftest-vda` after interop changes.

5. **The watchdog is the crash safety net.** SUDOVDA auto-removes the display ~3s after pings stop
   (`WatchdogPinger`, interval = `(watchdog_timeout_seconds*1000)/3` ms, but **clamped to a 250ms
   minimum** via `Math.Max(250, ...)` regardless of the driver timeout). Don't block the ping thread
   for long. Crash recovery also relies on the persisted `session.state.json`
   (`VirtualDisplaySession.RecoverIfNeeded`, called at startup BEFORE the detector starts). The
   snapshot is written **after `CreateDisplay` but before `Apply`/topology changes**, and deleted
   after restore on Deactivate — so a crash mid-session always leaves enough to restore the physical
   topology (the watchdog reaps the orphan display ~3s later; `RecoverIfNeeded` restores on next launch).

6. **Run in the interactive user session** (`app.manifest` = `asInvoker`). Display changes and VD's
   named objects are unreliable from session 0 — do NOT turn this into a Windows Service. The SUDOVDA
   device SD grants Everyone read/write, so no elevation is needed. If a CCD/IOCTL call ever returns
   ACCESS_DENIED, the manifest fallback is `requireAdministrator`. (Note: `SetDisplayConfig` err=5
   ACCESS_DENIED also occurs transiently when *another process* — Virtual Desktop — is concurrently
   reconfiguring displays; see the topology-race section, that case is NOT fixed by elevation.)

7. **DPI is set in code** (`Application.SetHighDpiMode(PerMonitorV2)`); the manifest deliberately
   **omits `dpiAware`** — declaring it in both places throws at startup.

8. **PowerShell 5.1 parses no-BOM UTF-8 `.ps1` files as ANSI.** Keep `.ps1` files **ASCII-only**
   (an em-dash in a string broke `build.ps1`). `.cs`/`.md` are fine (Roslyn/editors handle UTF-8);
   diagnostics report files are written with explicit UTF-8.

9. **Detection is multi-signal and config-driven (live).** The default **`auto`** mode rule is
   `streamer running && (LAN-private peer on VD ports 38810/38820/38830/38840/38850 || BodyStateEvent
   pulse)`. Modes **`ports`** and **`event`** check only the one corresponding signal; the streamer is
   **always** required. The public **cloud-relay** connection on 38810 (e.g. 40.x Azure) is filtered
   out by `VdSignals.IsPrivate` (loopback + non-RFC1918 rejected). Debounce is timestamp-based
   (`TickCount64`): connect default 1500ms, disconnect default 3000ms (longer to suppress flicker).
   `EvaluateRaw()` is read live each poll, so `Mode`/debounce/ports apply without restart.
   **On the dev machine the signal that actually fires is the PORTS/LAN-peer one** (the event pulse
   stays 0); headset calibration is **done** and confirmed in real VR.

10. **Don't regenerate `MonitorGuid`.** `Config.EnsureDefaults()` mints one with `Guid.NewGuid()` only
    when empty (first run), then persists it forever. Its stability is what lets Windows persist the
    virtual display's layout/identity across sessions — regenerating it breaks that memory (not exposed
    in the UI).

11. **The per-session mode enforcer fights Virtual Desktop's resolution override.**
    `VirtualDisplaySession.StartModeEnforcer/StopModeEnforcer` runs a background `Task` that, roughly
    every **2s**, calls `DisplayManager.EnforceMode(handle, res)` to re-assert the configured 4K120
    when the virtual display drifts. **Why it exists:** Virtual Desktop OVERRIDES the virtual display's
    resolution to its own (lower) stream setting *after* it connects. `EnforceMode` reads the current
    mode via `GdiInterop.TryGetCurrentMode` and on drift tries legacy `SetMode` (works on 24H2 — see
    #1) then `SetModeViaCcd` as a fallback. The enforcer is **intrinsic to the session** (NOT the
    detector/orchestrator), runs under the session `_gate`, checks `_handle` still matches before
    re-applying, and exits cleanly if the session was removed out-of-band. **Always** `StopModeEnforcer()`
    before the session goes inactive to avoid an orphaned task. Limitation: `EnforceMode` only corrects
    *resolution drift* — it cannot recover a **full deactivation** (when the display goes inactive,
    `FindGdiNameForTarget` returns empty). See the topology-race section.

## Known issue: VD-vs-SUDOVDA topology race (open, intermittent)

This is the current top open item. Not yet fixed.

- **Symptom:** sometimes on entering VR the physical monitors blank ~1s then come back **without** the
  virtual display activating, leaving disabled `Linux FHD` monitors.
- **Root cause:** TWO virtual-display systems race at connect — Virtual Desktop's **OWN** native
  virtual monitor (`Linux FHD` / `LNX0000`) and **our** SUDOVDA (`Legacy Moonli` / `SMKD1CE`) both
  reconfigure the desktop topology at the same time.
- **Mechanism:** `SetDisplayConfig` returns **err=5 (ERROR_ACCESS_DENIED)** when another process (VD)
  is concurrently changing displays, so our `ApplyExclusive` / `Restore` **silently fail**. Worst case
  is when the VD Streamer is **killed** (disconnect logs "streamer not running") → `Restore` returns
  err=5 on **all** fallback paths (supplied snapshot / `SDC_USE_DATABASE_CURRENT` / `SDC_TOPOLOGY_EXTEND`),
  retried up to ~4x within the same flux window. Crash-recovery on the next launch eventually fixes it
  (`USE_DATABASE_CURRENT` succeeds once the flux is over).
- **Enforcer side effects:** when the config resolution != VD's stream resolution, the enforcer can
  **fight VD continuously** (100+ corrections every 2s for minutes); and it **cannot** recover a full
  deactivation (only resolution drift), because `EnforceMode`'s `FindGdiNameForTarget` returns empty
  while the display is inactive.
- **Orphans:** SUDOVDA monitor instances accumulate (UID256..265 observed) when `RemoveDisplay`
  returns `False`.
- **PLANNED mitigations (NOT YET IMPLEMENTED):**
  - (a) Resolve the dual-virtual-display conflict — detect whether VD's own virtual-display feature is
    enabled and disable one side.
  - (b) Settle-delay after CONNECT + verify the virtual is actually active/primary a beat after
    `ApplyExclusive` and re-apply if VD clobbered it; treat err=5 as **retry-after-wait**, not hard-fail.
  - (c) Enforcer detects full deactivation & re-activates, and backs off (slows cadence) when it
    detects a fight.
  - (d) An ACCESS_DENIED-aware retry loop on the streamer-killed restore.
  - (e) Orphan reaping + forensic logging around `Activate` (today it logs `apply ok=True` even when
    the visual result actually failed).

## Conventions

- **Interop:** `DllImport` (not `LibraryImport`) for consistency; blittable structs; by-value IOCTL
  helpers (`unmanaged` generics: `IoctlInOut`/`IoctlIn`/`IoctlOut`/`IoctlNone`). Interop types are
  `internal`; controllers expose `internal` methods when they surface interop structs (avoid public
  exposure → accessibility errors).
- **Threading:** controllers/session/orchestrator/updater each lock a private `_gate` (reentrant
  Monitor), not a global lock. The detector raises events on its background thread; manual tray
  activate/deactivate run on `Task.Run`; UI updates marshal through `TrayApp`'s hidden control.
- **Logging over assertions:** there's no test project; verify via selftests + log inspection.
- **Config is read live** by the detector each poll, so most settings apply without restart. **But
  resolution applies on next *activation*** — while a session is active, the mode enforcer keeps it at
  the value captured at Activate time; editing the config resolution mid-session won't take effect
  until the next Activate.

## Shipping & self-update

The global specs in `Z:\global .md\` (`ship.md`, `updater.md`) are the authority for the process;
this repo implements the .NET equivalents. **Repo:** `robogears/AutomaticVRVDCreator`
(https://github.com/robogears/AutomaticVRVDCreator — **exists, public**, origin set, `gh` authed as
`robogears`). **Asset:** `AutoVRVD-win-x64.exe` (single-file, self-contained — the substring
`win-x64.exe` is what the updater matches; don't break it).

**In-app updater** (`Update/`): `Updater.cs` polls `releases/latest`, compares the assembly version to
the tag (numeric semver, e.g. `0.1.10 > 0.1.2`), **searches assets for a name containing
`win-x64.exe` (case-insensitive)** and downloads the first match — or, if no match / not Windows
(`CanSelfInstall()` false), **opens the release page in the browser** instead. Download goes to
`%TEMP%\AutoVRVD-update-{ts}.exe`; `ApplyUpdate` swaps-and-relaunches via a detached `.cmd` (retries
`move /Y` for up to 30s, relaunches, self-deletes) and the app calls `Application.Exit()` immediately.
`UpdateController.cs` drives the tray menu state machine; launch check is silent (`UpdateCheckOnLaunch`,
default true — only surfaces a balloon if an update is available). The download runs on a worker
thread (`Task.Run`) and the progress callback is **state-guarded** (ignores reports once state !=
Downloading) so a trailing `100%` can't overwrite the final `Restart to apply` — without that, v0.1.1/
v0.1.2 stuck on `Downloading 100%` (fixed in v0.1.3). The `Owner` / `Repo` / `AssetSubstring` constants
in `Updater.cs` are hardcoded — update them if the repo is renamed or the asset is.

**Ship process — NEVER auto-ship; only on explicit "ship it / release vX.Y.Z".** Follow `ship.md`:
1. Bump `<Version>` in `src/AutoVRVD/AutoVRVD.csproj` (patch by default). This is the **current**
   version for local builds; **CI is the authority** — `release.yml` extracts the version FROM the tag
   (`v0.1.1` → `0.1.1`) and passes it via `-p:Version=` on publish, so the shipped exe's version ==
   the tag (the updater relies on this).
2. Overwrite `RELEASE_NOTES.md` entirely with the new version's body — keep the 4-part format
   (What's new / `---` / Install + Requirements / `---` / Full Changelog compare link). No old sections.
3. `git add` explicitly, commit, `git tag -a vX.Y.Z -m vX.Y.Z`, push main, push the tag.
4. The tag (matching `v*`) triggers `.github/workflows/release.yml` → builds the single-file exe →
   copies it to `AutoVRVD-win-x64.exe` → attaches it to a release created with **`draft: true`** via
   `softprops/action-gh-release@v2` (`body_path: RELEASE_NOTES.md`).
5. Run `.\ship-tail.ps1 vX.Y.Z` — waits for CI, **verifies the release body** (softprops sometimes
   leaves it empty; the script checks body length < 50 chars and, if so, re-applies via
   `gh release edit --notes-file RELEASE_NOTES.md`), prints the final draft status + asset list, and
   leaves it as a **draft**.
6. The user reviews and publishes manually (`gh release edit vX.Y.Z --draft=false`). Never flip
   `draft` in the workflow, and **never auto-publish a draft**.

**v0.1.1 is the first release** (commit `289cadf`): pushed, CI green, draft release created with asset
`AutoVRVD-win-x64.exe` and body from `RELEASE_NOTES.md` — **left as a DRAFT for the user to publish**.

## Status (2026-06-17)

- **Shipped & published v0.1.1 → v0.1.2 → v0.1.3** on the public repo. **v0.1.3 fixes the in-app
  updater** (it stuck on "Downloading 100%" — download now on a worker thread + state-guarded progress).
  Bootstrap caveat: the broken v0.1.1/v0.1.2 updater can't auto-fetch v0.1.3, so v0.1.3 needs a
  **one-time manual install** (download the asset, run it over the old copy); auto-update works after.
- **v0.1.2 — faithful monitor restore.** Physical monitors now return to their exact
  resolution/**refresh**/position/**primary** on disconnect (and crash recovery) via
  `DisplayManager.RestorePhysicalModes`: capture each physical monitor's `MonitorState`, then on
  restore re-apply mode via legacy ChangeDisplaySettingsEx + re-assert primary via a *fresh* CCD
  query. Root cause was the CCD-array restore failing `err=87` (stale virtual-display paths) and
  falling back to `USE_DATABASE_CURRENT`, which lost refresh/primary and *ratcheted* worse each session.
- **Core feature confirmed in real VR**; proven detection signal is **ports/LAN-peer** (event stays 0).
- **Top open item:** the **VD-vs-SUDOVDA topology race** above (intermittent failed activation / err=5
  restores / enforcer fights / orphan monitors). The user now also runs **Sunshine's VDD** and VD's own
  "Virtual Desktop Monitor" — several virtual-display systems coexist, which aggravates the race.
  Mitigations (a)–(e) designed, not implemented.
- **Possible follow-ups:** lock `Detection.Mode` to `ports`; orphan reaping; settle-delay + err=5 retry.
