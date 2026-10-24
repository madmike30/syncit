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
SetupIconFile=..\SyncIt.App\Resources\icon_idle.ico
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"

[Icons]
Name: "{autoprograms}\SyncIt"; Filename: "{app}\SyncIt.exe"
Name: "{autodesktop}\SyncIt"; Filename: "{app}\SyncIt.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SyncIt.exe"; Description: "Launch SyncIt"; Flags: nowait postinstall skipifsilent

; Settings and tokens in %APPDATA%\SyncIt are kept on uninstall on purpose (your sync state).
