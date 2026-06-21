# What's new in v0.1.3

## Updater fix
- Fixes the in-app updater getting **stuck on "Downloading 100%"** instead of advancing to **"Restart to apply"**. The download now runs off the UI thread (the tray no longer freezes during the ~68 MB download), and a trailing progress update can no longer overwrite the final state.

## Faithful monitor restore (from v0.1.2)
- Your monitors return to their **exact** resolution/**refresh**/position/**primary** after VR — no more 360 Hz dropping to 120 Hz or your secondary becoming primary.

---

# Install

- **Windows (x64)**: download `AutoVRVD-win-x64.exe` and run it (self-contained — no .NET install needed). On first launch SmartScreen may warn (unsigned): **More info → Run anyway**.
- **Updating from v0.1.2 or earlier:** the old updater is broken (the bug this release fixes), so **download this exe manually once** and run it over your existing copy. After you're on v0.1.3, the in-app auto-updater works for future releases.

Config and logs live in `%AppData%\AutoVRVD\`.

## Requirements

- Windows 10/11 x64.
- **Virtual Desktop Streamer 1.30+** running on the PC.
- The **SUDOVDA** virtual display driver installed and started (bundled with Apollo, or standalone from <https://github.com/SudoMaker/SudoVDA>).

---

**Full Changelog**: https://github.com/robogears/AutomaticVRVDCreator/compare/v0.1.2...v0.1.3
