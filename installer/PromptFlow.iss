#define MyAppName "PromptFlow"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "PromptFlow"
#define MyAppExeName "PromptFlow.exe"

[Setup]
AppId={{A7D69B9B-72D5-4F0C-8C15-1D50F16F0C5D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\PromptFlow
DefaultGroupName={#MyAppName}
OutputDir=output
OutputBaseFilename=PromptFlow-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\PromptFlow\Assets\PromptFlow.ico
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked
Name: "cleandata"; Description: "卸载时删除用户数据"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PromptFlow"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\PromptFlow.ico"
Name: "{autodesktop}\PromptFlow"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\PromptFlow.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 PromptFlow"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\PromptFlow"; Tasks: cleandata
