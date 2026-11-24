; Per-user setup for 冰蓝桌面. Built by installer\build.ps1, which publishes the app into
; installer\obj\publish and passes the version with /DAppVersion=x.y.z.

#ifndef AppVersion
  #define AppVersion "0.4.5"
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
CloseApplications=no
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
Name: "{autoprograms}\卸载冰蓝桌面"; Filename: "{uninstallexe}"
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
  UpdateBackupDir: String;
  InstallSucceeded: Boolean;

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

// The app starts a downloaded update silently with /UPDATE=1 and exits; the new version
// is opened again once the files are in place.
function RelaunchAfterUpdate(): Boolean;
begin
  Result := (ExpandConstant('{param:UPDATE|0}') = '1') and FileExists(ExpandConstant('{app}\BingLan.exe'));
end;

// Signals any running production BingLan instance via its documented exit event so it
// saves state, restores taskbar/desktop icons, and releases the single-instance mutex cleanly.
procedure SignalProductionExit();
begin
  RunPowerShell(
    '$ev = $null; ' +
    'if ([System.Threading.EventWaitHandle]::TryOpenExisting(''Local\BingLan.Desktop.Production.Exit'', [ref]$ev)) { ' +
    '  try { $null = $ev.Set() } finally { $ev.Dispose() } ' +
    '}');
end;

// Stops only the copy of the app installed here, not other programs with the same name.
// Signals graceful exit first, waits briefly, then forces and waits out WER on this exact path.
procedure StopInstalledApp(const ExePath: String; WaitGraceful: Boolean);
var
  GraceLoops: Integer;
begin
  if WaitGraceful then GraceLoops := 24 else GraceLoops := 16;
  SignalProductionExit();
  RunPowerShell(
    '$p = ' + PowerShellLiteral(ExePath) + '; ' +
    '$graceLoops = ' + IntToStr(GraceLoops) + '; ' +
    'for ($i = 0; $i -lt $graceLoops; $i++) { ' +
    '  $r = @(Get-Process -Name BingLan -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $p }); ' +
    '  if ($r.Count -eq 0) { exit 0 } ' +
    '  Start-Sleep -Milliseconds 250 ' +
    '} ' +
    'for ($i = 0; $i -lt 48; $i++) { ' +
    '  $r = @(Get-Process -Name BingLan -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $p }); ' +
    '  if ($r.Count -eq 0) { exit 0 } ' +
    '  $r | Stop-Process -Force -ErrorAction SilentlyContinue; ' +
    '  Start-Sleep -Milliseconds 250 ' +
    '}');
end;

// Stops another production BingLan copy running under the current user from a different path
// only if it still holds the production single-instance mutex after the graceful exit signal.
// Interactive QA / test copies use a separate mutex and are never terminated.
procedure StopOtherCopies(const ExePath: String);
begin
  SignalProductionExit();
  RunPowerShell(
    '$p = ' + PowerShellLiteral(ExePath) + '; ' +
    '$held = $false; ' +
    'for ($i = 0; $i -lt 20; $i++) { ' +
    '  $m = $null; ' +
    '  if (-not [System.Threading.Mutex]::TryOpenExisting(''Local\BingLan.Desktop.Production'', [ref]$m)) { exit 0 } ' +
    '  $m.Dispose(); $held = $true; Start-Sleep -Milliseconds 250 ' +
    '} ' +
    'if (-not $held) { exit 0 } ' +
    '$sid = [System.Diagnostics.Process]::GetCurrentProcess().SessionId; ' +
    '$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value; ' +
    '$others = @(Get-Process -Name BingLan -ErrorAction SilentlyContinue | ' +
    '  Where-Object { $_.SessionId -eq $sid -and $_.Path -ne $p }); ' +
    'foreach ($proc in $others) { ' +
    '  $w = Get-CimInstance Win32_Process -Filter (''ProcessId = '' + $proc.Id) -ErrorAction SilentlyContinue; ' +
    '  if (-not $w) { continue } ' +
    '  $cmd = [string]$w.CommandLine; ' +
    '  if ($cmd -match ''--interactive-qa'' -or ([string]$proc.Path) -match ''\\tests\\'') { continue } ' +
    '  $o = Invoke-CimMethod -InputObject $w -MethodName GetOwnerSid -ErrorAction SilentlyContinue; ' +
    '  if (-not $o -or $o.Sid -ne $me) { continue } ' +
    '  try { $null = $proc.CloseMainWindow(); if ($proc.WaitForExit(3000)) { continue } } catch {} ' +
    '  Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; ' +
    '  try { $null = $proc.WaitForExit(12000) } catch {} ' +
    '}');
end;

// Keeps a full rollback snapshot of the existing installation before overwrite so an aborted
// or partially failed update never leaves the user with a missing BingLan.exe.
function BackupInstalledApp(const AppDir, BackupDir: String): Boolean;
begin
  Result := RunPowerShell(
    '$src = ' + PowerShellLiteral(AppDir) + '; ' +
    '$dst = ' + PowerShellLiteral(BackupDir) + '; ' +
    'if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force -ErrorAction SilentlyContinue } ' +
    'Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force -ErrorAction Stop; ' +
    'if (-not (Test-Path -LiteralPath (Join-Path $dst ''BingLan.exe''))) { exit 1 }');
end;

procedure RestoreBackupApp(const BackupDir, AppDir: String);
begin
  RunPowerShell(
    '$src = ' + PowerShellLiteral(BackupDir) + '; ' +
    '$dst = ' + PowerShellLiteral(AppDir) + '; ' +
    'if (-not (Test-Path -LiteralPath (Join-Path $src ''BingLan.exe''))) { exit 1 } ' +
    'New-Item -ItemType Directory -Path $dst -Force | Out-Null; ' +
    'Get-ChildItem -LiteralPath $src -Force | ForEach-Object { ' +
    '  Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $dst $_.Name) -Recurse -Force -ErrorAction SilentlyContinue ' +
    '}; ' +
    'Remove-Item -LiteralPath $src -Recurse -Force -ErrorAction SilentlyContinue');
end;

procedure RemoveBackupDir(const BackupDir: String);
begin
  if (BackupDir <> '') and DirExists(BackupDir) then
    RunPowerShell('Remove-Item -LiteralPath ' + PowerShellLiteral(BackupDir) + ' -Recurse -Force -ErrorAction SilentlyContinue');
end;

// Before installing or updating, stops the installed copy (if any) and any other production copy,
// backs up the previous binaries for rollback, and undoes taskbar/desktop icon changes on update.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  AppDir: String;
  ExePath: String;
  BackupDir: String;
  ResultCode: Integer;
begin
  Result := '';
  InstallSucceeded := False;
  UpdateBackupDir := '';
  AppDir := ExpandConstant('{app}');
  ExePath := ExpandConstant('{app}\BingLan.exe');
  IsUpdate := FileExists(ExePath);
  if IsUpdate then
  begin
    StopInstalledApp(ExePath, ExpandConstant('{param:UPDATE|0}') = '1');
  end;
  StopOtherCopies(ExePath);
  if IsUpdate then
  begin
    Exec(ExePath, '--restore-taskbar', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    BackupDir := ExpandConstant('{tmp}\BingLan-Rollback-Backup');
    if BackupInstalledApp(AppDir, BackupDir) then
    begin
      UpdateBackupDir := BackupDir;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if FileExists(ExpandConstant('{app}\BingLan.exe')) then
    begin
      InstallSucceeded := True;
      RemoveBackupDir(UpdateBackupDir);
      UpdateBackupDir := '';
    end;
  end;
end;

procedure DeinitializeSetup();
var
  ExePath: String;
  ResultCode: Integer;
begin
  if IsUpdate and (not InstallSucceeded) and (UpdateBackupDir <> '') and DirExists(UpdateBackupDir) then
  begin
    RestoreBackupApp(UpdateBackupDir, ExpandConstant('{app}'));
    UpdateBackupDir := '';
    ExePath := ExpandConstant('{app}\BingLan.exe');
    if (ExpandConstant('{param:UPDATE|0}') = '1') and FileExists(ExePath) then
    begin
      Exec(ExePath, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    end;
  end;
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
    StopInstalledApp(ExePath, False);

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
