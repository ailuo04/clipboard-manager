#ifndef AppVersion
  #error AppVersion must be provided by installer.ps1
#endif
#ifndef SourceDir
  #error SourceDir must be provided by installer.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be provided by installer.ps1
#endif

[Setup]
AppId={{8BCDA40E-D12B-4A21-BBD5-EF1653BD6F7B}
AppName=剪贴板
AppVersion={#AppVersion}
AppVerName=剪贴板 {#AppVersion}
AppPublisher=ailuo04
AppPublisherURL=https://github.com/ailuo04/clipboard-manager
DefaultDirName={localappdata}\Programs\ClipboardManager
DefaultGroupName=剪贴板
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
DisableProgramGroupPage=yes
UsePreviousTasks=no
AppMutex=Local\ClipboardManager.Desktop.v1
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=剪贴板
UninstallDisplayIcon={app}\ClipboardManager.App.exe
SetupIconFile=..\Assets\Clipboard.ico
OutputDir={#OutputDir}
OutputBaseFilename=ClipboardManager-{#AppVersion}-win-x64-setup
VersionInfoVersion={#AppVersion}.0
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[InstallDelete]
Type: files; Name: "{app}\ClipboardManager.exe"
Type: files; Name: "{app}\ClipboardManager.Update.exe"
Type: files; Name: "{app}\ClipboardManager.dll"
Type: files; Name: "{app}\ClipboardManager.runtimeconfig.json"
Type: files; Name: "{app}\ClipboardManager.deps.json"
Type: filesandordirs; Name: "{app}\runtime"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\剪贴板\剪贴板"; Filename: "{app}\ClipboardManager.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\剪贴板"; Filename: "{app}\ClipboardManager.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ClipboardManager.App.exe"; Description: "启动剪贴板"; Flags: nowait postinstall skipifsilent
