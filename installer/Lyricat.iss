#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{1663F824-1D7F-4ADC-8F2D-BDFD697D6A74}
AppName=Lyricat
AppVersion={#AppVersion}
AppVerName=Lyricat {#AppVersion}
AppPublisher=Kiisum!
AppPublisherURL=https://github.com/PanConKisu/lyricat
AppSupportURL=https://github.com/PanConKisu/lyricat/issues
DefaultDirName={localappdata}\Programs\Lyricat
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=Lyricat-Setup
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\Lyricat.exe
UninstallDisplayName=Lyricat
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Kiisum.Lyricat
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Start Lyricat with Windows (it waits quietly in the tray)"

[Files]
Source: "{#SourceDir}\Lyricat.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Lyricat"; Filename: "{app}\Lyricat.exe"
Name: "{userdesktop}\Lyricat"; Filename: "{app}\Lyricat.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Lyricat"; \
  ValueData: """{app}\Lyricat.exe"" --startup"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Lyricat.exe"; Description: "Open Lyricat"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\Lyricat.exe.old"
Type: files; Name: "{app}\Lyricat.exe.new"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep <> usUninstall then exit;
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Lyricat');
  if UninstallSilent then exit;
  if MsgBox('Also delete your saved accounts, settings and stats?' #13#10#13#10 +
            'Choose No if you plan to install Lyricat again and want to keep them.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    Exec(ExpandConstant('{app}\Lyricat.exe'), '--forget', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;
