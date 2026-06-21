; VirtualMirage installer (Inno Setup 6). Per-machine install to Program Files.
; Build:  ISCC.exe /DAppVersion=0.1.5 installer\VirtualMirage.iss
; CI passes the version from the tag; AppVersion defaults to 0.0.0 for local test builds.
; Expects the published single-file exe at ..\publish\VirtualMirage.exe.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{2BD7950A-B9C3-46EC-BE57-2B3297A62E37}
AppName=VirtualMirage
AppVersion={#AppVersion}
AppVerName=VirtualMirage {#AppVersion}
AppPublisher=robogears
AppPublisherURL=https://github.com/robogears/VirtualMirage
AppSupportURL=https://github.com/robogears/VirtualMirage/issues
AppUpdatesURL=https://github.com/robogears/VirtualMirage/releases
DefaultDirName={autopf}\VirtualMirage
DefaultGroupName=VirtualMirage
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\installer-out
OutputBaseFilename=VirtualMirage-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\VirtualMirage\VirtualMirage.ico
UninstallDisplayIcon={app}\VirtualMirage.exe
UninstallDisplayName=VirtualMirage
VersionInfoVersion={#AppVersion}
VersionInfoCompany=robogears
VersionInfoProductName=VirtualMirage

; Cleanly close a running VirtualMirage before replacing its files (used on install + silent update).
CloseApplications=yes
CloseApplicationsFilter=VirtualMirage.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start VirtualMirage automatically when I sign in"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\VirtualMirage.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\VirtualMirage"; Filename: "{app}\VirtualMirage.exe"
Name: "{autodesktop}\VirtualMirage"; Filename: "{app}\VirtualMirage.exe"; Tasks: desktopicon

[Run]
; Fresh interactive install only: enable per-user autostart AS THE SIGNED-IN USER (writes HKCU, not the
; elevated admin hive). Skipped on silent auto-updates so it doesn't override the user's later choice.
Filename: "{app}\VirtualMirage.exe"; Parameters: "--set-autostart"; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: autostart; Check: not WizardSilent
; Interactive install: optional "Launch VirtualMirage" checkbox on the Finished page (runs de-elevated).
Filename: "{app}\VirtualMirage.exe"; Description: "Launch VirtualMirage"; Flags: runasoriginaluser nowait postinstall skipifsilent
; Silent (auto-update) install: relaunch the tray app as the signed-in user.
Filename: "{app}\VirtualMirage.exe"; Flags: runasoriginaluser nowait; Check: WizardSilent

; Note: the autostart entry is a per-user HKCU "Run" value (written by --set-autostart as the signed-in
; user). The elevated uninstaller can't reach that user's hive, so we don't remove it here; a stale Run
; value pointing at the removed exe is harmless (Windows skips missing autostart targets). The user can
; also toggle it any time in the app's Settings ("Start with Windows").
