#define MyAppName "Subtitle & Video Compressor"
#define MyAppVersion "1.2.4"
#define MyAppPublisher "Subtitle Video Tool"

[Setup]
AppId={{2CB8256C-1C82-4A7B-AB15-FDD9CB07AD98}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\SubtitleVideoTool
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputDir=..\release
OutputBaseFilename=SubtitleVideoTool-Setup-v{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName}
SetupIconFile=..\assets\SubtitleVideoTool.ico
UninstallDisplayIcon={app}\assets\SubtitleVideoTool.ico

[Files]
Source: "..\SubtitleVideoTool.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\SubtitleVideoTool.vbs"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Uninstall.vbs"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README-FA.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LOGO-PROMPT-FA.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHECKSUMS-SHA256.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\tools\*"; DestDir: "{app}\tools"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\third_party\*"; DestDir: "{app}\third_party"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\assets\*"; DestDir: "{app}\assets"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{sys}\wscript.exe"; Parameters: """{app}\SubtitleVideoTool.vbs"""; WorkingDir: "{app}"; IconFilename: "{app}\assets\SubtitleVideoTool.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{sys}\wscript.exe"; Parameters: """{app}\SubtitleVideoTool.vbs"""; WorkingDir: "{app}"; IconFilename: "{app}\assets\SubtitleVideoTool.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{sys}\wscript.exe"; Parameters: """{app}\SubtitleVideoTool.vbs"""; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
