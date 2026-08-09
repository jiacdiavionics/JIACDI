; DIMP (Drone Industrial Mission Planner) Inno Setup Script
; Creates an EXE installer for DIMP

#define MyAppName "DIMP"
#define MyAppVersion "1.3.83"
#define MyAppPublisher "Mohammed Shdifat"
#define MyAppURL "https://github.com/jiacdiavionics/JIACDI"
#define MyAppExeName "DIMP.exe"

#if !FileExists("bin\Release\net461\DIMP.exe")
  #error "Release build missing: bin\Release\net461\DIMP.exe"
#endif
#if !FileExists("bin\Release\net461\Windows11.mpsystheme")
  #error "Modern theme missing: bin\Release\net461\Windows11.mpsystheme"
#endif
#if !FileExists("bin\Release\net461\Tools\scrcpy\scrcpy.exe")
  #error "Bundled scrcpy missing from the Release output"
#endif
#if !FileExists("ExtLibs\wasm\wwwroot\Cesium\Cesium.js")
  #error "Bundled Cesium runtime missing"
#endif
#if !FileExists("map3d\vehicles\fixedwing.glb") || \
    !FileExists("map3d\vehicles\quadcopter.glb") || \
    !FileExists("map3d\vehicles\hexacopter.glb") || \
    !FileExists("map3d\vehicles\helicopter.glb")
  #error "One or more bundled 3D vehicle models are missing"
#endif
#if !FileExists("sitl\ArduCopter.exe") || \
    !FileExists("sitl\ArduHeli.exe") || \
    !FileExists("sitl\ArduPlane.exe") || \
    !FileExists("sitl\ArduRover.exe")
  #error "One or more bundled SITL vehicle executables are missing"
#endif
#if !FileExists("sitl\cygatomic-1.dll") || \
    !FileExists("sitl\cyggcc_s-1.dll") || \
    !FileExists("sitl\cyggcc_s-seh-1.dll") || \
    !FileExists("sitl\cyggomp-1.dll") || \
    !FileExists("sitl\cygiconv-2.dll") || \
    !FileExists("sitl\cygintl-8.dll") || \
    !FileExists("sitl\cygquadmath-0.dll") || \
    !FileExists("sitl\cygssp-0.dll") || \
    !FileExists("sitl\cygstdc++-6.dll") || \
    !FileExists("sitl\cygwin1.dll")
  #error "One or more bundled SITL runtime DLLs are missing"
#endif
#if !FileExists("sitl\sim_vehicle.py") || \
    !FileExists("sitl\vehicleinfo.py") || \
    !FileExists("sitl\vehicleinfo.json") || \
    !FileExists("sitl\models\plane.parm") || \
    !FileExists("sitl\models\skywalker_2013.json") || \
    !FileExists("sitl\default_params\copter.parm") || \
    !FileExists("sitl\default_params\copter-heli.parm") || \
    !FileExists("sitl\default_params\rover.parm")
  #error "Bundled SITL support/default parameter files are incomplete"
#endif
#if !DirExists("C:\ProgramData\Mission Planner\gmapcache\TileDBv3\en\GoogleSatelliteMap")
  #error "GMap satellite cache source is missing"
#endif
#if !FileExists("C:\ProgramData\Mission Planner\srtm\N31E035.hgt")
  #error "Expected SRTM terrain source tile N31E035.hgt is missing"
#endif
#if !FileExists("map3d\buildings3d\tileset.json")
  #error "Offline 3D building tileset is missing"
#endif

[Setup]
; Basic installer info
AppId={{8A9F3D5E-4B2C-4E8A-9F1D-7C6B3A5E8D9F}
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
OutputBaseFilename=DIMP-{#MyAppVersion}-Setup
SetupIconFile=mpdesktop.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ChangesAssociations=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1; Check: not IsAdminInstallMode

[InstallDelete]
Type: files; Name: "{userdocs}\Mission Planner\sitl\*.new"

[Files]
; Main application files from bin\Release\net461
Source: "bin\Release\net461\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Bundled local Cesium runtime for offline 3D Map
Source: "ExtLibs\wasm\wwwroot\Cesium\*"; DestDir: "{app}\Cesium"; Flags: ignoreversion recursesubdirs createallsubdirs
; Bundled UAV models selected from MAVLink vehicle type
Source: "map3d\vehicles\*"; DestDir: "{app}\Map3D\vehicles"; Flags: ignoreversion recursesubdirs createallsubdirs
; Bundled SITL simulator files for Simulation tab
Source: "sitl\*"; DestDir: "{userdocs}\Mission Planner\sitl"; Excludes: "eeprom.bin,logs\*,*.new"; Flags: ignoreversion recursesubdirs createallsubdirs
; Bundled offline/prefetched 2D GMap cache
Source: "C:\ProgramData\Mission Planner\gmapcache\*"; DestDir: "{commonappdata}\Mission Planner\gmapcache"; Flags: ignoreversion recursesubdirs createallsubdirs onlyifdoesntexist uninsneveruninstall
; Bundled offline 3D terrain source tiles
Source: "C:\ProgramData\Mission Planner\srtm\*.hgt"; DestDir: "{commonappdata}\Mission Planner\srtm"; Flags: ignoreversion onlyifdoesntexist uninsneveruninstall
; Bundled terrain-seated OSM buildings with photo-style facade and roof textures.
; The versioned destination ensures upgrades do not keep loading the old white package.
Source: "map3d\buildings3d\*"; DestDir: "{commonappdata}\Mission Planner\map3d\buildings3d-v2-textured"; Flags: ignoreversion recursesubdirs createallsubdirs onlyifdoesntexist uninsneveruninstall
; Note: Don't include other user data directories or temp files

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: quicklaunchicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Registry]
; File associations for telemetry logs
Root: HKA; Subkey: "Software\Classes\.tlog"; ValueType: string; ValueName: ""; ValueData: "DIMP.tlog"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\DIMP.tlog"; ValueType: string; ValueName: ""; ValueData: "Telemetry Log"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\DIMP.tlog\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

Root: HKA; Subkey: "Software\Classes\.dfbin"; ValueType: string; ValueName: ""; ValueData: "DIMP.dfbin"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\DIMP.dfbin"; ValueType: string; ValueName: ""; ValueData: "Binary Log"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\DIMP.dfbin\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

Root: HKA; Subkey: "Software\Classes\.log"; ValueType: string; ValueName: ""; ValueData: "DIMP.log"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\DIMP.log"; ValueType: string; ValueName: ""; ValueData: "DF Log"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\DIMP.log\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

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
    MsgBox('DIMP requires Microsoft .NET Framework 4.7.2 or later.' + #13#10 + #13#10 +
           'Please install .NET Framework 4.7.2 from:' + #13#10 +
           'https://dotnet.microsoft.com/download/dotnet-framework/net472' + #13#10 + #13#10 +
           'Then run this installer again.', mbError, MB_OK);
    Result := False;
  end;
end;
