; Inno Setup script for SyncIt. Build: iscc /DAppVersion=1.0.0 /DSourceExe=..\publish\SyncIt.exe installer\SyncIt.iss
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\SyncIt.exe"
#endif

[Setup]
AppId={{6B8F3C2E-5A41-4D7B-9E3A-2C1F0D9B7A11}
AppName=SyncIt
AppVersion={#AppVersion}
AppPublisher=SyncIt contributors
AppPublisherURL=https://github.com/madmike30/syncit
DefaultDirName={localappdata}\Programs\SyncIt
DefaultGroupName=SyncIt
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=SyncIt-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\SyncIt.exe
LicenseFile=..\LICENSE
SetupIconFile=assets\app.ico
WizardStyle=modern
WizardSizePercent=110
WizardImageFile=assets\wizard-large.bmp,assets\wizard-large-2x.bmp
WizardSmallImageFile=assets\wizard-small.bmp,assets\wizard-small-2x.bmp
DisableWelcomePage=no
DisableDirPage=auto
DisableReadyPage=yes
ShowLanguageDialog=no
AppCopyright=Copyright (c) 2026 SyncIt contributors
VersionInfoVersion={#AppVersion}
UninstallDisplayName=SyncIt
CloseApplications=yes

[Messages]
WelcomeLabel2=This will install SyncIt {#AppVersion} on your computer.%n%nSyncIt keeps a folder on this PC and a folder in your Google Drive in sync. No admin rights needed.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"

[Icons]
Name: "{autoprograms}\SyncIt"; Filename: "{app}\SyncIt.exe"
Name: "{autodesktop}\SyncIt"; Filename: "{app}\SyncIt.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SyncIt.exe"; Description: "Open SyncIt now"; Flags: nowait postinstall skipifsilent
; In-app updates run the installer with /SILENT /RELAUNCH=1 so SyncIt starts again afterwards.
Filename: "{app}\SyncIt.exe"; Flags: nowait; Check: ShouldRelaunch

; Settings and tokens in %APPDATA%\SyncIt are kept on uninstall on purpose (your sync state).

[UninstallRun]
; Remove the Explorer right-click "SyncIt" commands (registered per user by the app).
Filename: "{app}\SyncIt.exe"; Parameters: "--unregister-shell"; Flags: runhidden skipifdoesntexist; RunOnceId: "UnregisterShell"

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
