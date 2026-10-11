; Per-user setup for 冰蓝桌面. Built by installer\build.ps1, which publishes the app into
; installer\obj\publish and passes the version with /DAppVersion=x.y.z.

#ifndef AppVersion
  #define AppVersion "0.2.13"
#endif

[Setup]
AppId={{6DCB614F-9EE3-4A65-AA58-B132F45F12F8}
AppName=冰蓝桌面
AppVersion={#AppVersion}
AppVerName=冰蓝桌面 {#AppVersion}
AppPublisher=BingLan
DefaultDirName={localappdata}\Programs\BingLan
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=bin
OutputBaseFilename=BingLan-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\BingLan.App\Assets\Brand\BingLan.ico
UninstallDisplayIcon={app}\BingLan.exe
; Tells Explorer to refresh its icon cache, so a new app icon shows after an update.
ChangesAssociations=yes
UninstallDisplayName=冰蓝桌面
CloseApplications=force
RestartApplications=no

[Languages]
Name: "zh"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "startup"; Description: "登录 Windows 时启动冰蓝桌面"
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "obj\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\docs\USER-GUIDE.md"; DestDir: "{app}"; DestName: "使用说明.md"; Flags: ignoreversion
; Licences of the open fonts built into the app (SIL Open Font License 1.1).
Source: "..\src\BingLan.App\Assets\Fonts\LICENSE-*.txt"; DestDir: "{app}\字体许可"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\冰蓝桌面"; Filename: "{app}\BingLan.exe"
Name: "{autoprograms}\冰蓝桌面使用说明"; Filename: "{app}\使用说明.md"
Name: "{autodesktop}\冰蓝桌面"; Filename: "{app}\BingLan.exe"; Tasks: desktopicon

[Registry]
; The app's single startup entry; the app can switch it off. Uninstall removes it in
; CurUninstallStepChanged, only while it still starts this installation.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "BingLan"; ValueData: """{app}\BingLan.exe"""; Tasks: startup; Check: KeepsStartupChoice

[Run]
Filename: "{app}\BingLan.exe"; Description: "启动冰蓝桌面"; Flags: nowait postinstall skipifsilent
Filename: "{app}\BingLan.exe"; Flags: nowait; Check: RelaunchAfterUpdate

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  RestoreFailed: Boolean;
  IsUpdate: Boolean;

// Text as a single-quoted PowerShell string.
function PowerShellLiteral(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
  Result := '''' + Result + '''';
end;

function RunPowerShell(const Script: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "' + Script + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Stops only the copy of the app installed here, not other programs with the same name.
procedure StopInstalledApp(const ExePath: String);
begin
  RunPowerShell(
    'Get-Process -Name BingLan -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -eq ' + PowerShellLiteral(ExePath) + ' } | ' +
    'Stop-Process -Force; Start-Sleep -Milliseconds 500');
end;

// Before an update replaces the files, the running copy is stopped and its taskbar and
// desktop icon changes are undone, so nothing stays changed if the new copy never starts.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExePath: String;
  ResultCode: Integer;
begin
  Result := '';
  ExePath := ExpandConstant('{app}\BingLan.exe');
  IsUpdate := FileExists(ExePath);
  if IsUpdate then
  begin
    StopInstalledApp(ExePath);
    Exec(ExePath, '--restore-taskbar', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

// The app starts a downloaded update silently with /UPDATE=1 and exits; the new version
// is opened again once the files are in place.
function RelaunchAfterUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

// An update keeps the startup choice made in the app: the entry is written again only
// while it is still there.
function KeepsStartupChoice(): Boolean;
begin
  Result := (not IsUpdate) or RegValueExists(HKCU, RunKey, 'BingLan');
end;

// Personal data is only removed on an explicit "Yes" (the default is "No"), and it goes
// to the Recycle Bin so an accidental answer can be undone.
function MoveToRecycleBin(const Path: String): Boolean;
begin
  Result := RunPowerShell(
    'Add-Type -AssemblyName Microsoft.VisualBasic; ' +
    '[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(' + PowerShellLiteral(Path) + ', ' +
    '''OnlyErrorDialogs'', ''SendToRecycleBin'')');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  ExePath: String;
  StartupCommand: String;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    ExePath := ExpandConstant('{app}\BingLan.exe');
    StopInstalledApp(ExePath);

    // Undo any taskbar or desktop icon change the app or a crashed run left behind.
    // A failed restore keeps its record, but the app is being removed, so say how to
    // put things back by hand.
    RestoreFailed := not Exec(ExePath, '--restore-taskbar', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
      or (ResultCode <> 0);
    if RestoreFailed and not UninstallSilent then
      MsgBox('没有能自动恢复任务栏或桌面图标。' #13#10 #13#10 +
             '任务栏：在 Windows 设置 > 个性化 > 任务栏中调整。' #13#10 +
             '桌面图标：在桌面右键菜单“查看”中勾选“显示桌面图标”。' #13#10 #13#10 +
             '个人数据会保留，其中的恢复记录可在重新安装后自动恢复。',
             mbError, MB_OK);

    // The app can add its startup entry itself, so the entry is removed whether or not
    // the installer created it, as long as it starts exactly this installation.
    if RegQueryStringValue(HKCU, RunKey, 'BingLan', StartupCommand) then
    begin
      StringChangeEx(StartupCommand, '"', '', True);
      if CompareText(Trim(StartupCommand), ExePath) = 0 then
        RegDeleteValue(HKCU, RunKey, 'BingLan');
    end;
  end;

  // After a failed restore the data folder holds the only record of what to undo, so
  // it is kept without asking.
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent and not RestoreFailed then
  begin
    DataDir := ExpandConstant('{localappdata}\BingLanWidgets');
    if DirExists(DataDir) and
       (MsgBox('是否同时删除冰蓝桌面的个人数据？' #13#10 #13#10 +
               '包括桌面组件、待办、便签、文件映射、设置和备份，位于：' #13#10 + DataDir + #13#10 #13#10 +
               '选择“是”会把这些数据移到回收站；选择“否”会保留，重新安装后继续使用。',
               mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) and
       not MoveToRecycleBin(DataDir) then
      MsgBox('没有能把个人数据移到回收站，数据仍保留在：' #13#10 + DataDir, mbError, MB_OK);
  end;
end;
