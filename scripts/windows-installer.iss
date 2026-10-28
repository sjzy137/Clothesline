; Inno Setup script for the Windows build of Clothesline.
; The setup compiles to a single self-contained executable, so the installed app has no runtime
; dependency on a machine-wide .NET installation.
;
; Both paths can be overridden on the ISCC command line, for example:
;   ISCC.exe /dMyPublishDir=C:\build\publish /dMyOutputDir=C:\dist scripts\windows-installer.iss

#define MyAppName "Clothesline"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Clothesline contributors"
#define MyAppURL "https://github.com/zhongguozhang/Clothesline"
#define MyAppExeName "Clothesline.exe"

; A fixed AppId keeps upgrades and the uninstall entry tied to this product rather than to a path.
#define MyAppId "{{F01D7E58-BF6C-4044-82BC-95DD803FDCCD}"

#ifndef MyPublishDir
  #define MyPublishDir "..\_publish"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\..\dist"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
; Install for the current user by default, which keeps the trial free of elevation prompts.
; Launching the setup as administrator instead offers a machine-wide install, because
; {autopf} resolves to Program Files only when the setup is elevated.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Clothesline
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; .NET 10 does not run on Windows 8.1 or earlier, so refuse there rather than installing
; something that cannot start.
MinVersion=10.0
; The published output is win-x64. x64compatible allows an x64 host as well as ARM64 running
; x64 emulation, and blocks a 32-bit host that could never load the executable.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE
SetupIconFile=..\assets\clothesline.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir={#MyOutputDir}
OutputBaseFilename=Clothesline-Setup-{#MyAppVersion}-windows-x64
; The payload is a single large self-contained image; maximum LZMA2 is worth the compile time.
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.ExplorerGroup=资源管理器集成：
chinesesimplified.ExplorerAdd=在右键菜单中加入「挂到晾衣绳」与「发送到 → Clothesline」
english.ExplorerGroup=Explorer integration:
english.ExplorerAdd=Add "Hang on Clothesline" and "Send to > Clothesline" to the right-click menu

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "explorer"; Description: "{cm:ExplorerAdd}"; GroupDescription: "{cm:ExplorerGroup}"

[Files]
Source: "{#MyPublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
; The Send to menu is just a folder of shortcuts, and Windows hands the selected files to the
; target as arguments, which is the shape Clothesline already accepts. Unlike a classic shell
; verb it stays in the normal Windows 11 right-click menu instead of behind "Show more options".
; {usersendto} rather than the legacy {sendto}: the latter resolves to the all-users folder, which
; a per-user install that never asks for elevation cannot write to.
Name: "{usersendto}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: explorer

[Registry]
; Classic shell verb, registered per user so no elevation is needed. Windows 11 keeps this behind
; "Show more options"; the Send to entry above covers the main menu, so both are installed.
Root: HKCU; Subkey: "Software\Classes\*\shell\ClotheslineHang"; ValueType: string; ValueName: ""; ValueData: "挂到晾衣绳"; Tasks: explorer; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\ClotheslineHang"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName},0"; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\*\shell\ClotheslineHang"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\*\shell\ClotheslineHang\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: explorer; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ClotheslineHang"; ValueType: string; ValueName: ""; ValueData: "挂到晾衣绳"; Tasks: explorer; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ClotheslineHang"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName},0"; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ClotheslineHang"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ClotheslineHang\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: explorer; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

; Preferences and the pinned/dismissed state live in %LOCALAPPDATA%\Clothesline, outside {app},
; so they are deliberately not touched here: uninstalling then reinstalling keeps your line intact.
