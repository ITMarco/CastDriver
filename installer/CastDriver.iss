; Inno Setup script for CastDriver — per-user install (no admin), so the in-app self-updater
; can still overwrite the exe in place. release.ps1 passes AppVersion/SourceExe/IconFile/OutputDir
; via /D; the defaults below let it also be compiled standalone from the repo root.

#define AppName "CastDriver"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\CastDriver-standalone.exe"
#endif
#ifndef IconFile
  #define IconFile "..\CastDriver.UI\icon.ico"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
; Stable AppId so upgrades/uninstall track the same product across versions.
AppId={{A1F4C2E8-6B3D-4E7A-9F21-7C5D8B0E9A34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ITMarco
AppPublisherURL=https://github.com/ITMarco/CastDriver
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=CastDriver-Setup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\CastDriver.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#AppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "CastDriver.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\CastDriver.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\CastDriver.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CastDriver.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
