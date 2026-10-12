; Inno Setup script: per-user install (no admin prompt), Start menu entry, optional start with Windows.
#define AppVersion "1.0.0"

[Setup]
AppId={{6A3C1E52-5B7E-4E7B-9A51-1F0C2B8E4D11}
AppName=LSearch
AppVersion={#AppVersion}
AppPublisher=scutoid
DefaultDirName={localappdata}\Programs\LSearch
DefaultGroupName=LSearch
PrivilegesRequired=lowest
OutputBaseFilename=LSearch-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\LSearch.exe
DisableProgramGroupPage=yes

[Files]
Source: "..\dist\LSearch\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Tasks]
Name: "startup"; Description: "Start LSearch when Windows starts"

[Icons]
Name: "{userprograms}\LSearch"; Filename: "{app}\LSearch.exe"
Name: "{userstartup}\LSearch"; Filename: "{app}\LSearch.exe"; Tasks: startup

[Run]
Filename: "{app}\LSearch.exe"; Parameters: "--show"; Description: "Launch LSearch"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/F /IM lsearch-backend.exe /IM LSearch.exe /IM llama-server.exe"; Flags: runhidden; RunOnceId: "KillLSearch"
