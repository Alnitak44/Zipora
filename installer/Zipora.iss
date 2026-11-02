; AppId and install scope must remain stable across all future releases.
#ifndef MyAppVersion
  #define MyAppVersion "2.0.39"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\standard"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

[Setup]
AppId=Alnitak44.Zipora
AppName=Zipora
AppVersion={#MyAppVersion}
AppPublisher=Alnitak44
AppPublisherURL=https://github.com/Alnitak44/Zipora
AppSupportURL=https://github.com/Alnitak44/Zipora/issues
AppUpdatesURL=https://github.com/Alnitak44/Zipora/releases/latest
DefaultDirName={autopf}\Zipora
UsePreviousAppDir=yes
UsePreviousTasks=yes
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
AppMutex=Zipora.Desktop.Running,Global\Zipora.Desktop.Running
CloseApplications=yes
CloseApplicationsFilter=Zipora.exe
RestartApplications=no
RestartIfNeededByRun=no
AllowRootDirectory=no
AllowNetworkDrive=no
AllowUNCPath=no
ChangesAssociations=yes
UninstallDisplayIcon={app}\Zipora.exe
UninstallDisplayName=Zipora
LicenseFile=..\LICENSE
SetupIconFile=..\src\Zipora\Assets\Zipora.ico
WizardStyle=modern
ShowLanguageDialog=auto
Compression=lzma2
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=Zipora-v{#MyAppVersion}-Setup-x64
VersionInfoVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
english.DesktopShortcut=Create a desktop shortcut
english.ExplorerIntegration=Add Zipora to the File Explorer context menu
english.LaunchZipora=Launch Zipora
english.PortableTarget=This folder contains a portable installation. Choose a separate desktop installation directory. Its Data folder will not be migrated or overwritten.
chinesesimp.DesktopShortcut=创建桌面快捷方式
chinesesimp.ExplorerIntegration=将 Zipora 添加到资源管理器右键菜单
chinesesimp.LaunchZipora=启动 Zipora
chinesesimp.PortableTarget=此目录包含便携版，请选择独立的常规版安装目录。便携版 Data 不会被迁移或覆盖。

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: unchecked
Name: "contextmenu"; Description: "{cm:ExplorerIntegration}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "Zipora.ShellExtension.dll"; Flags: ignoreversion recursesubdirs createallsubdirs
; Explorer can retain this DLL after Zipora exits. Defer replacement to reboot
; when necessary, rather than forcibly terminating Explorer or archive tasks.
Source: "{#SourceDir}\Zipora.ShellExtension.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace

[Icons]
Name: "{autoprograms}\Zipora"; Filename: "{app}\Zipora.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Zipora"; Filename: "{app}\Zipora.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\Zipora.exe"; ValueType: string; ValueData: "{app}\Zipora.exe"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\CLSID\{{B53761D2-18A8-4C23-B6A6-948C2FBE8F4A}\InprocServer32"; ValueType: string; ValueData: "{app}\Zipora.ShellExtension.dll"; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\CLSID\{{B53761D2-18A8-4C23-B6A6-948C2FBE8F4A}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\AllFilesystemObjects\shell\Zipora"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Zipora"; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\AllFilesystemObjects\shell\Zipora"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Zipora.ico"",0"; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\AllFilesystemObjects\shell\Zipora"; ValueType: string; ValueName: "ExplorerCommandHandler"; ValueData: "{{B53761D2-18A8-4C23-B6A6-948C2FBE8F4A}"; Tasks: contextmenu

[Run]
Filename: "{app}\Zipora.exe"; Description: "{cm:LaunchZipora}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\portable.json')) or
     FileExists(ExpandConstant('{app}\App\Zipora.exe')) then
    Result := CustomMessage('PortableTarget');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RegisteredDll: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKLM64,
      'Software\Classes\CLSID\{B53761D2-18A8-4C23-B6A6-948C2FBE8F4A}\InprocServer32',
      '', RegisteredDll) then
      if CompareText(RegisteredDll, ExpandConstant('{app}\Zipora.ShellExtension.dll')) = 0 then
      begin
        RegDeleteKeyIncludingSubkeys(HKLM64, 'Software\Classes\AllFilesystemObjects\shell\Zipora');
        RegDeleteKeyIncludingSubkeys(HKLM64, 'Software\Classes\CLSID\{B53761D2-18A8-4C23-B6A6-948C2FBE8F4A}');
      end;
end;
