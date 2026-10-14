; Inno Setup script: per-user install (no admin prompt), Start menu entry, optional start with Windows.
; WinSpotlight is a fork of LSearch with its own AppId, so both can be installed side by side.
#define AppVersion "1.1.0"

[Setup]
AppId={{8A3B3934-2303-42DA-9A79-E2AC9CFDABA1}
AppName=WinSpotlight
AppVersion={#AppVersion}
AppPublisher=xTony7
AppPublisherURL=https://github.com/xTony7/WinSpotlight
DefaultDirName={localappdata}\Programs\WinSpotlight
DefaultGroupName=WinSpotlight
PrivilegesRequired=lowest
OutputBaseFilename=WinSpotlight-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\WinSpotlight.exe
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
en.StartWithWindows=Start WinSpotlight when Windows starts
es.StartWithWindows=Iniciar WinSpotlight al arrancar Windows
en.LaunchApp=Launch WinSpotlight
es.LaunchApp=Abrir WinSpotlight

[Files]
Source: "..\dist\WinSpotlight\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Tasks]
Name: "startup"; Description: "{cm:StartWithWindows}"

[Icons]
Name: "{userprograms}\WinSpotlight"; Filename: "{app}\WinSpotlight.exe"
Name: "{userstartup}\WinSpotlight"; Filename: "{app}\WinSpotlight.exe"; Tasks: startup

[Run]
Filename: "{app}\WinSpotlight.exe"; Parameters: "--show"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/F /IM winspotlight-backend.exe /IM WinSpotlight.exe /IM llama-server.exe"; Flags: runhidden; RunOnceId: "KillWinSpotlight"
