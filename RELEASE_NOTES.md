# What's new in v0.1.11

## Save & replay your display layouts (duplicate the virtual display to a monitor)
- New buttons in **Settings** (shown when *"Disable my physical monitors"* is **off**): **Save current as Non-VR layout** and **Save current as VR layout**. Arrange your desktop exactly how you want it — for example, the virtual display **duplicated onto one monitor** with the rest off — then save it. VirtualMirage reproduces your **VR layout on connect** and restores your **Non-VR layout on disconnect**, automatically. It's fully **opt-in**: if you don't save a VR layout, nothing about today's behavior changes.
- Why it's done this way: Windows handles the (cross-adapter) duplicate perfectly when you set it up by hand, so VirtualMirage just snapshots that exact arrangement and replays it — no fragile guessing.

> Tip: set the virtual display's **Resolution** to match the monitor you're mirroring onto (e.g. **1920×1080**) so the duplicate is clean.

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

**Full Changelog**: https://github.com/robogears/VirtualMirage/compare/v0.1.10...v0.1.11
