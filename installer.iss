; Inno Setup Script for Geometry Swarm
; Professional Windows Installer with Full Godot .NET Runtime Support

#define MyAppName "Geometry Swarm"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Hilal Muhammad Rizqullah"
#define MyAppExeName "GeometrySwarm.exe"

[Setup]
AppId={{6F431662-D0F7-441A-9214-79540A19E216}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=installer
OutputBaseFilename=GeometrySwarm_Setup_v{#MyAppVersion}
SetupIconFile=icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
UninstallDisplayIcon={app}\icon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "build\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\GeometrySwarm.pck"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\data_GeometrySwarm_windows_x86_64\*"; DestDir: "{app}\data_GeometrySwarm_windows_x86_64"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "build\GodotSharp\*"; DestDir: "{app}\GodotSharp"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "build\.godot\*"; DestDir: "{app}\.godot"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\icon.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\icon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
