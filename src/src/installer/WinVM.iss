; WinVM 1.6.0 installer (Inno Setup 6.x)
; Build:  ISCC.exe /DPublishDir=<publish\WinVM> /DOutDir=<output folder> installer\WinVM.iss
#define AppName "WinVM"
#define AppVersion "1.6.0"
#define AppPublisher "WinVM"
#define AppExe "WinVM.exe"
#ifndef PublishDir
  #define PublishDir "..\publish\WinVM"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

[Setup]
AppId={{6E0B1C5A-4F3D-4C8B-9A61-57A1D2B0C915}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppComments=Hyper-V 虚拟机管理工具
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=WinVM {#AppVersion} 安装程序
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; admin -> Program Files; "仅为我安装"/no admin -> %LOCALAPPDATA%\Programs
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
OutputDir={#OutDir}
OutputBaseFilename=WinVM-{#AppVersion}-Setup
SetupIconFile=..\Assets\winvm.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "chs"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Hyper-V 虚拟机管理"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "启动 WinVM"; Flags: nowait postinstall skipifsilent
