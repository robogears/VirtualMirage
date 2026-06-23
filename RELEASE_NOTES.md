# What's new in v0.1.13

## Fix: loading a VR layout restores only the monitors you saved
- Loading a saved VR layout was turning on **all** your monitors instead of just the ones in the layout. It now activates **exactly** the virtual display + the monitors you saved (matched by their stable hardware ID) and turns the rest off — so a layout of "monitor 3 + the virtual display" comes back as just those two.

> Note: monitors in a saved VR layout currently come back **extended**. Auto-mirroring (duplicating the virtual onto a monitor) is still in progress; for now you can Win+P → Duplicate once it's loaded.

---

# Install

- **Recommended — installer:** download **`VirtualMirage-Setup.exe`** and run it — no admin, no UAC. SmartScreen may warn on the unsigned build: **More info -> Run anyway**.
- **Portable alternative:** **`VirtualMirage-win-x64.exe`** is a self-contained single exe (no install, no auto-update).
- **Updating from v0.1.8+:** tray -> **Check for updates -> Download & install** (silent, no prompts).

Config and logs live in `%AppData%\VirtualMirage\`.

## Requirements

- Windows 10/11 x64.
- **Virtual Desktop Streamer 1.30+** running on the PC.
- The **SUDOVDA** virtual display driver installed and started (bundled with Apollo, or standalone from <https://github.com/SudoMaker/SudoVDA>).

---

**Full Changelog**: https://github.com/robogears/VirtualMirage/compare/v0.1.12...v0.1.13
