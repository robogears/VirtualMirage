# What's new in v0.1.4

## Proper Windows installer
- VirtualMirage now ships as a real **`VirtualMirage-Setup.exe`** installer: it installs to `C:\Program Files\VirtualMirage`, adds a **Start Menu** shortcut and an **Add/Remove Programs** entry, optionally **starts at sign-in**, and launches when finished. (Per-machine install, so it asks for admin once.)
- **In-app updates now go through the installer.** When an update is available, the tray's **Update available... -> Restart to apply** downloads the new setup and installs it silently (one UAC confirm), then relaunches.

## A proper app icon
- VirtualMirage finally has a real **Windows logo** — a VR-headset mark that shows on the Start-Menu shortcut, the taskbar, Add/Remove Programs, the installer itself, and the **system-tray icon** (now badged with a status color: gray = idle, blue = VR connected, green = virtual display active, red = error).

---

# Install

- **Recommended — installer:** download **`VirtualMirage-Setup.exe`** and run it. SmartScreen may warn on the unsigned build: **More info -> Run anyway**.
- **Portable alternative:** **`VirtualMirage-win-x64.exe`** is a self-contained single exe you can run from anywhere (no install, no auto-update).
- **Updating from v0.1.3:** the in-app updater works now — it will fetch this release for you. To move from the portable exe to a proper installed app, just download and run `VirtualMirage-Setup.exe` once.

Config and logs live in `%AppData%\VirtualMirage\`.

## Requirements

- Windows 10/11 x64.
- **Virtual Desktop Streamer 1.30+** running on the PC.
- The **SUDOVDA** virtual display driver installed and started (bundled with Apollo, or standalone from <https://github.com/SudoMaker/SudoVDA>).

---

**Full Changelog**: https://github.com/robogears/VirtualMirage/compare/v0.1.3...v0.1.4
