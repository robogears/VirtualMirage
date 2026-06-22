# What's new in v0.1.8

## No more UAC prompts — silent updates
- VirtualMirage now installs **per-user** (to `%LocalAppData%\Programs\VirtualMirage`) instead of system-wide. That means **no admin rights and no UAC prompt** — on install *or* on updates. Clicking **Check for updates -> Download & install** now applies the update **silently in the background** and relaunches, with zero prompts. (The app never needed admin: display changes run in your session, the SUDOVDA driver grants access, and autostart is a per-user setting.)

## Moving from an older (per-machine) build
- If you have **v0.1.7 or earlier** installed (in `C:\Program Files`), do a one-time cut-over: **uninstall** the old "VirtualMirage" from Add/Remove Programs (the last UAC prompt you'll ever see), then run this `VirtualMirage-Setup.exe` once. After that, updates are silent forever.

---

# Install

- **Recommended — installer:** download **`VirtualMirage-Setup.exe`** and run it — **no admin, no UAC**. SmartScreen may still warn on the unsigned build: **More info -> Run anyway**.
- **Portable alternative:** **`VirtualMirage-win-x64.exe`** is a self-contained single exe you can run from anywhere (no install, no auto-update).

Config and logs live in `%AppData%\VirtualMirage\`.

## Requirements

- Windows 10/11 x64.
- **Virtual Desktop Streamer 1.30+** running on the PC.
- The **SUDOVDA** virtual display driver installed and started (bundled with Apollo, or standalone from <https://github.com/SudoMaker/SudoVDA>).

---

**Full Changelog**: https://github.com/robogears/VirtualMirage/compare/v0.1.7...v0.1.8
