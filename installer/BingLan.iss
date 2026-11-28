; Per-user setup for 冰蓝桌面. Built by installer\build.ps1, which publishes the app into
; installer\obj\publish and passes the version with /DAppVersion=x.y.z.

#ifndef AppVersion
  #define AppVersion "0.7.1"
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
CreateUninstallRegKey=ArePackageFilesVerified
CloseApplications=no
RestartApplications=no

[Languages]
Name: "zh"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "startup"; Description: "登录 Windows 时启动冰蓝桌面"
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[InstallDelete]
; SHA-256 of obsolete runtime files from the v0.2.63 and v0.2.64 portable packages.
; InstallDelete runs before [Files], so a dependency included again will be installed.
Type: files; Name: "{app}\Microsoft.VisualBasic.Forms.dll"; Check: IsObsoleteRuntimeFile('Microsoft.VisualBasic.Forms.dll', '640f7fc1fcd098e04f3f8d03668039719cfe799d2a12eda7a9c6a2e9bc1e2acf', '9ec2b355dc54744ad583b95301e7b98e16a7398fd73a91e09da141cd4338f235')
Type: files; Name: "{app}\Microsoft.Windows.SDK.NET.dll"; Check: IsObsoleteRuntimeFile('Microsoft.Windows.SDK.NET.dll', '0ec371d93798852e36461c8adddbeadce0f963a04752f0b64e54fe19c1c834a7', '')
Type: files; Name: "{app}\System.Design.dll"; Check: IsObsoleteRuntimeFile('System.Design.dll', '50250d023c53b39520dff62d7263e70cd432e2a2355b8b16320ced2dcf0fcd08', '38e1692b3a318305e414e1a90ddfc50c31f7e94b2f359507e95c8ee2ff865f31')
Type: files; Name: "{app}\System.Drawing.Design.dll"; Check: IsObsoleteRuntimeFile('System.Drawing.Design.dll', 'c2a5c1f2d68683bf2b2d5207f07cd73cc90bc8ea26d82691d8633fb6b10b1d9a', '2ea6eef09fef23d0ba0704c21b5a76c342006e4fb9cea9bdc948cc7927eb61af')
Type: files; Name: "{app}\System.Windows.Forms.Design.Editors.dll"; Check: IsObsoleteRuntimeFile('System.Windows.Forms.Design.Editors.dll', 'c2ae228226d9d83601722c8801260a3907f52d4176b87e40470677ec61ef0758', 'c6ce67b9a2e53c1abdd6791209e83be2ed72e34d52643ec55f96b23f6abb756f')
Type: files; Name: "{app}\System.Windows.Forms.Design.dll"; Check: IsObsoleteRuntimeFile('System.Windows.Forms.Design.dll', '4b96cf16ea44e11627be57f7ebd5580e7553547d83eb5dc61121ff0d84eff4b3', 'f15d79f9c205a8189a38c753caa5a2c556bf601099d889d96df4b6c9bc86d1ae')
Type: files; Name: "{app}\System.Windows.Forms.Primitives.dll"; Check: IsObsoleteRuntimeFile('System.Windows.Forms.Primitives.dll', '1ce7325bd95c3998fb34b158bd99c3b2105ec6627e4e878f2089c8b77e23cc96', 'dac7659c0440b15657f3500f17b7c54d97e7e40e43dd8563904090d43a93e947')
Type: files; Name: "{app}\System.Windows.Forms.dll"; Check: IsObsoleteRuntimeFile('System.Windows.Forms.dll', 'a178f2f0de086c30e743ce32b8bfbb150e802e1bab235ff14525b4a6e2227df9', 'e64a7d4cc4988dc78f8231c8b2a2a501d3464f70e461fc72253207b194b3901d')
Type: files; Name: "{app}\WinRT.Runtime.dll"; Check: IsObsoleteRuntimeFile('WinRT.Runtime.dll', 'bcf3a14e8712a90837fc5c0d8c8a24696af2bd7f74e9767597ec81ededfb23db', '')
Type: files; Name: "{app}\WindowsFormsIntegration.dll"; Check: IsObsoleteRuntimeFile('WindowsFormsIntegration.dll', '994cb84f5f46df57be5ea9ed3de1c6b703212b9985a28440806fc131c7d0119f', 'd2d133c35fe183f6924d8502d73ab3ee4d341a99d0b44dbfbb6af71af7b132d1')
Type: files; Name: "{app}\mscordaccore_amd64_amd64_10.0.1126.37416.dll"; Check: IsObsoleteRuntimeFile('mscordaccore_amd64_amd64_10.0.1126.37416.dll', 'c1b92da5356bb36f4ab55aa54b2d25a8a27518cf7a456eb4957ddfe94e2d428c', '')

[Files]
Source: "obj\publish-sha256.txt"; Flags: dontcopy
Source: "obj\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; Licences of the open fonts built into the app (SIL Open Font License 1.1).
Source: "..\src\BingLan.App\Assets\Fonts\LICENSE-*.txt"; DestDir: "{app}\字体许可"; Flags: ignoreversion
; The last payload entry verifies the files before Setup creates shortcuts or registers the new version.
Source: "..\docs\USER-GUIDE.md"; DestDir: "{app}"; DestName: "使用说明.md"; Flags: ignoreversion; AfterInstall: VerifyInstalledFiles

[Icons]
Name: "{autoprograms}\冰蓝桌面"; Filename: "{app}\BingLan.exe"; Check: ArePackageFilesVerified
Name: "{autoprograms}\冰蓝桌面使用说明"; Filename: "{app}\使用说明.md"; Check: ArePackageFilesVerified
Name: "{autoprograms}\卸载冰蓝桌面"; Filename: "{uninstallexe}"; Check: ArePackageFilesVerified
Name: "{autodesktop}\冰蓝桌面"; Filename: "{app}\BingLan.exe"; Tasks: desktopicon; Check: ArePackageFilesVerified

[Registry]
; The app's single startup entry; the app can switch it off. Uninstall removes it in
; CurUninstallStepChanged, only while it still starts this installation.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "BingLan"; ValueData: """{app}\BingLan.exe"""; Tasks: startup; Check: KeepsStartupChoice

[Run]
Filename: "{app}\BingLan.exe"; Description: "启动冰蓝桌面"; Flags: nowait postinstall skipifsilent; Check: IsVerifiedInstallation

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  FileAttributeReparsePoint = $400;

var
  RestoreFailed: Boolean;
  IsUpdate: Boolean;
  UpdateBackupDir: String;
  FailedUninstallerName: String;
  PackageFiles: TArrayOfString;
  PackageFilesVerified: Boolean;
  InstallStarted: Boolean;
  InstallSucceeded: Boolean;

function IsObsoleteRuntimeFile(const Name, FirstHash, SecondHash: String): Boolean;
var
  Path, Digest: String;
begin
  Result := False;
  if not IsUpdate or (UpdateBackupDir = '') then exit;
  Path := ExpandConstant('{app}\') + Name;
  if not FileExists(Path) then exit;
  try
    Digest := GetSHA256OfFile(Path);
    Result := (CompareText(Digest, FirstHash) = 0) or
      ((SecondHash <> '') and (CompareText(Digest, SecondHash) = 0));
  except
    Log('Skipped unreadable runtime file: ' + Name);
  end;
end;

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

function IsVerifiedInstallation(): Boolean;
begin
  Result := InstallSucceeded and (ExpandConstant('{param:UPDATE|0}') <> '1');
end;

function ArePackageFilesVerified(): Boolean;
begin
  Result := PackageFilesVerified;
end;

procedure LoadPackageManifest();
var
  Entry, RelativeName, Segments: String;
  I: Integer;
begin
  ExtractTemporaryFile('publish-sha256.txt');
  if not LoadStringsFromFile(ExpandConstant('{tmp}\publish-sha256.txt'), PackageFiles) or
    (GetArrayLength(PackageFiles) = 0) then
    RaiseException('Could not read the packaged file hashes.');
  for I := 0 to GetArrayLength(PackageFiles) - 1 do
  begin
    Entry := PackageFiles[I];
    RelativeName := Copy(Entry, 67, Length(Entry));
    Segments := '\' + RelativeName + '\';
    if (Copy(Entry, 65, 2) <> '  ') or (Pos('\\', Segments) <> 0) or
      (Pos('\..\', Segments) <> 0) or (Pos('\.\', Segments) <> 0) or
      (Pos(':', RelativeName) <> 0) or (Pos('/', RelativeName) <> 0) or
      (Pos('*', RelativeName) <> 0) or (Pos('?', RelativeName) <> 0) then
      RaiseException('Invalid packaged file hash entry.');
  end;
end;

procedure VerifyInstalledFile(const Name, Digest: String);
var
  Path: String;
begin
  Path := ExpandConstant('{app}\') + Name;
  if not FileExists(Path) then
    RaiseException('Missing installed file: ' + Name);
  if CompareText(GetSHA256OfFile(Path), Digest) <> 0 then
    RaiseException('Installed file hash mismatch: ' + Name);
end;

procedure VerifyInstalledFiles();
var
  Entry: String;
  I: Integer;
begin
  try
    for I := 0 to GetArrayLength(PackageFiles) - 1 do
    begin
      Entry := PackageFiles[I];
      VerifyInstalledFile(Copy(Entry, 67, Length(Entry)), Copy(Entry, 1, 64));
    end;
    PackageFilesVerified := True;
  except
    Log(GetExceptionMessage);
  end;
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
    'Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force -ErrorAction Stop; ' +
    'if (-not (Test-Path -LiteralPath (Join-Path $dst ''BingLan.exe''))) { exit 1 }');
end;

function RestoreBackupApp(const BackupDir, AppDir: String): Boolean;
begin
  Result := RunPowerShell(
    '$ErrorActionPreference = ''Stop''; ' +
    '$src = ' + PowerShellLiteral(BackupDir) + '; ' +
    '$dst = ' + PowerShellLiteral(AppDir) + '; ' +
    'if (-not (Test-Path -LiteralPath (Join-Path $src ''BingLan.exe''))) { exit 1 } ' +
    'New-Item -ItemType Directory -Path $dst -Force | Out-Null; ' +
    'Get-ChildItem -LiteralPath $src -Force | ForEach-Object { ' +
    '  Copy-Item -LiteralPath $_.FullName -Destination $dst -Recurse -Force ' +
    '}');
end;

function HasReparsePoint(const AppDir, RelativeName: String): Boolean;
var
  Name: String;
  Attributes: TFindRec;
  IsReparsePoint: Boolean;
begin
  Result := True;
  Name := AppDir + '\' + RelativeName;
  repeat
    if FindFirst(Name, Attributes) then
    begin
      IsReparsePoint := (Attributes.Attributes and FileAttributeReparsePoint) <> 0;
      FindClose(Attributes);
      if IsReparsePoint then exit;
    end;
    if CompareText(Name, AppDir) = 0 then break;
    Name := ExtractFileDir(Name);
  until Name = '';
  Result := False;
end;

function RemoveNewInstalledFile(const BackupDir, AppDir, Name: String): Boolean;
var
  Path: String;
begin
  Result := True;
  Path := AppDir + '\' + Name;
  if FileExists(Path) and not FileExists(BackupDir + '\' + Name) then
  begin
    if HasReparsePoint(AppDir, Name) then Result := False
    else Result := DeleteFile(Path);
    if not Result then Log('Could not remove the newly installed file: ' + Name);
  end;
end;

function RemoveInstallAdditions(const BackupDir, AppDir: String): Boolean;
var
  I: Integer;
  Entry: String;
begin
  Result := True;
  for I := 0 to GetArrayLength(PackageFiles) - 1 do
  begin
    Entry := PackageFiles[I];
    if not RemoveNewInstalledFile(BackupDir, AppDir, Copy(Entry, 67, Length(Entry))) then
      Result := False;
  end;
  if FailedUninstallerName = '' then exit;
  if not RemoveNewInstalledFile(BackupDir, AppDir, FailedUninstallerName) then Result := False;
  if not RemoveNewInstalledFile(BackupDir, AppDir, ChangeFileExt(FailedUninstallerName, '.dat')) then Result := False;
  if not RemoveNewInstalledFile(BackupDir, AppDir, ChangeFileExt(FailedUninstallerName, '.msg')) then Result := False;
end;

procedure RemoveBackupDir(const BackupDir: String);
begin
  if (BackupDir <> '') and DirExists(BackupDir) then
    RunPowerShell('Remove-Item -LiteralPath ' + PowerShellLiteral(BackupDir) + ' -Recurse -Force -ErrorAction SilentlyContinue');
end;

// Preserve the existing installation before stopping it or overwriting any files.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  AppDir: String;
  ExePath: String;
  BackupDir: String;
  ResultCode: Integer;
begin
  Result := '';
  PackageFilesVerified := False;
  InstallStarted := False;
  InstallSucceeded := False;
  UpdateBackupDir := '';
  FailedUninstallerName := '';
  try
    LoadPackageManifest();
  except
    Log(GetExceptionMessage);
    Result := '无法读取安装包的文件校验清单。';
    exit;
  end;
  AppDir := ExpandConstant('{app}');
  ExePath := ExpandConstant('{app}\BingLan.exe');
  IsUpdate := FileExists(ExePath);
  if IsUpdate then
  begin
    // Setup cleans {tmp} on exit, so a backup needed after a failed restore lives outside it.
    BackupDir := ExpandConstant('{localappdata}\BingLan-Rollback-') + ExtractFileName(ExpandConstant('{tmp}'));
    if DirExists(BackupDir) or FileExists(BackupDir) then
    begin
      Result := '旧版本备份位置已被占用，安装已停止。';
      exit;
    end;
    if not BackupInstalledApp(AppDir, BackupDir) then
    begin
      RemoveBackupDir(BackupDir);
      Result := '无法备份旧版本，安装已停止。';
      exit;
    end;
    UpdateBackupDir := BackupDir;
    StopInstalledApp(ExePath, ExpandConstant('{param:UPDATE|0}') = '1');
  end;
  StopOtherCopies(ExePath);
  if IsUpdate then
  begin
    Exec(ExePath, '--restore-taskbar', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  UninstallerPath: String;
begin
  if CurStep = ssInstall then InstallStarted := True;
  if CurStep <> ssPostInstall then exit;
  if not PackageFilesVerified then
  begin
    // Inno can allocate a new uninstaller slot when updating a portable copy.
    UninstallerPath := ExpandConstant('{uninstallexe}');
    if CompareText(ExtractFileDir(UninstallerPath), ExpandConstant('{app}')) = 0 then
      FailedUninstallerName := ExtractFileName(UninstallerPath);
    exit;
  end;
  InstallSucceeded := True;
  RemoveBackupDir(UpdateBackupDir);
  UpdateBackupDir := '';
  if ExpandConstant('{param:UPDATE|0}') = '1' then
    if not Exec(ExpandConstant('{app}\BingLan.exe'), '', ExpandConstant('{app}'),
      SW_SHOWNORMAL, ewNoWait, ResultCode) then
      Log('Could not reopen BingLan: ' + SysErrorMessage(ResultCode));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID <> wpFinished) or PackageFilesVerified then exit;
  WizardForm.FinishedHeadingLabel.Caption := '安装未完成';
  if IsUpdate then
    WizardForm.FinishedLabel.Caption := '程序文件校验失败。关闭安装程序后将恢复原版本。'
  else
    WizardForm.FinishedLabel.Caption := '程序文件校验失败。请重新运行安装程序。';
end;

function GetCustomSetupExitCode(): Integer;
begin
  if PackageFilesVerified then Result := 0 else Result := 10;
end;

procedure DeinitializeSetup();
var
  AppDir, ExePath: String;
  ResultCode: Integer;
  RestoreError: String;
  Restored: Boolean;
begin
  if IsUpdate and (not InstallSucceeded) and (UpdateBackupDir <> '') and DirExists(UpdateBackupDir) then
  begin
    AppDir := ExpandConstant('{app}');
    Restored := RestoreBackupApp(UpdateBackupDir, AppDir);
    if Restored and InstallStarted then Restored := RemoveInstallAdditions(UpdateBackupDir, AppDir);
    if not Restored then
    begin
      RestoreError := '无法完整恢复旧版本。旧版本备份位置' + #13#10 + UpdateBackupDir;
      Log(RestoreError);
      SuppressibleMsgBox(RestoreError, mbError, MB_OK, IDOK);
      exit;
    end;
    RemoveBackupDir(UpdateBackupDir);
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
  Result := PackageFilesVerified and ((not IsUpdate) or RegValueExists(HKCU, RunKey, 'BingLan'));
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
