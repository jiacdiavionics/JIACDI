; Mission Planner Inno Setup Script
; Creates an EXE installer for Mission Planner

#define MyAppName "Mission Planner"
#define MyAppVersion "1.3.83"
#define MyAppPublisher "Michael Oborne"
#define MyAppURL "https://ardupilot.org/planner/"
#define MyAppExeName "MissionPlanner.exe"

[Setup]
; Basic installer info
AppId={{625389D7-EB3C-4d77-A5F6-A285CF99437D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=Msi\licence.rtf
OutputDir=bin\installer
OutputBaseFilename=MissionPlanner-{#MyAppVersion}-Setup
SetupIconFile=mpdesktop.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1; Check: not IsAdminInstallMode

[Files]
; Main application files from bin\Release\net461
Source: "bin\Release\net461\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Note: Don't include user data directories or temp files

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: quicklaunchicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Registry]
; File associations for telemetry logs
Root: HKCR; Subkey: ".tlog"; ValueType: string; ValueName: ""; ValueData: "MissionPlanner.tlog"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "MissionPlanner.tlog"; ValueType: string; ValueName: ""; ValueData: "Telemetry Log"; Flags: uninsdeletekey
Root: HKCR; Subkey: "MissionPlanner.tlog\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

Root: HKCR; Subkey: ".dfbin"; ValueType: string; ValueName: ""; ValueData: "MissionPlanner.dfbin"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "MissionPlanner.dfbin"; ValueType: string; ValueName: ""; ValueData: "Binary Log"; Flags: uninsdeletekey
Root: HKCR; Subkey: "MissionPlanner.dfbin\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

Root: HKCR; Subkey: ".log"; ValueType: string; ValueName: ""; ValueData: "MissionPlanner.log"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "MissionPlanner.log"; ValueType: string; ValueName: ""; ValueData: "DF Log"; Flags: uninsdeletekey
Root: HKCR; Subkey: "MissionPlanner.log\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Code]
// Check for .NET Framework 4.7.2 or higher
function IsDotNet472Installed: Boolean;
var
  ResultCode: Cardinal;
begin
  Result := True;
  if RegKeyExists(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full') then
  begin
    if not RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', ResultCode) then
      Result := False
    else
      // 461808 = .NET Framework 4.7.2
      Result := (ResultCode >= 461808);
  end
  else
    Result := False;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not IsDotNet472Installed then
  begin
    MsgBox('Mission Planner requires Microsoft .NET Framework 4.7.2 or later.' + #13#10 + #13#10 +
           'Please install .NET Framework 4.7.2 from:' + #13#10 +
           'https://dotnet.microsoft.com/download/dotnet-framework/net472' + #13#10 + #13#10 +
           'Then run this installer again.', mbError, MB_OK);
    Result := False;
  end;
end;