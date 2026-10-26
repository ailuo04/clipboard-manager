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
AppMutex=Local\ClipboardManager.Desktop.v1
CloseApplications=no
RestartApplications=no
UninstallDisplayName=剪贴板
UninstallDisplayIcon={app}\ClipboardManager.exe
SetupIconFile=..\Assets\Clipboard.ico
OutputDir={#OutputDir}
OutputBaseFilename=ClipboardManager-{#AppVersion}-win-x64-setup
VersionInfoVersion={#AppVersion}.0
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\剪贴板\剪贴板"; Filename: "{app}\ClipboardManager.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\剪贴板"; Filename: "{app}\ClipboardManager.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ClipboardManager.exe"; Description: "启动剪贴板"; Flags: nowait postinstall skipifsilent unchecked
