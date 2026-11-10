; Inno Setup script for the PC Remote Center Windows server.
; Per-user install (no administrator rights needed); the server itself never asks for elevation.
; Build: pwsh -File Scripts\package-release.ps1   (calls ISCC with /DAppVersion=...)

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

[Setup]
AppId={{8F1C5E0A-3B7D-4C59-9E43-6A2F0D7B91C4}
AppName=PC Remote Center Server
AppVersion={#AppVersion}
AppPublisher=PC Remote Center
AppPublisherURL=https://github.com/unupunct/PC-Remote-Center
AppSupportURL=https://github.com/unupunct/PC-Remote-Center/issues
DefaultDirName={autopf}\PC Remote Center
DefaultGroupName=PC Remote Center
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist\installer
OutputBaseFilename=PCRemoteCenter-Server-Setup-{#AppVersion}
SetupIconFile=..\WindowsServer\RemoteControlServer\Assets\app.ico
UninstallDisplayIcon={app}\RemoteControlServer.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start PC Remote Center with &Windows (minimised to the tray)"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\dist\server\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Documentation\*.md"; DestDir: "{app}\Documentation"; Flags: ignoreversion

[Icons]
Name: "{group}\PC Remote Center"; Filename: "{app}\RemoteControlServer.exe"
Name: "{group}\Uninstall PC Remote Center"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PC Remote Center"; Filename: "{app}\RemoteControlServer.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PCRemoteCenter"; ValueData: """{app}\RemoteControlServer.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\RemoteControlServer.exe"; Description: "Start PC Remote Center now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; stop the running server before files are removed
Filename: "{sys}\taskkill.exe"; Parameters: "/IM RemoteControlServer.exe /F"; Flags: runhidden; RunOnceId: "StopServer"

[Code]
// Settings, paired devices, certificate and logs live in %APPDATA%\PCRemoteCenter and are kept unless the user chooses otherwise.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\PCRemoteCenter');
    if DirExists(DataDir) then
      if MsgBox('Also delete your settings, paired devices, TLS certificate and logs?' + #13#10 + #13#10 + DataDir + #13#10 + #13#10 +
                'Choose No to keep them (recommended if you plan to reinstall).', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
