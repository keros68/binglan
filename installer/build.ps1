# Builds installer\bin\BingLan-Setup-<version>.exe: a self-contained win-x64 publish of the
# app wrapped in a per-user Inno Setup installer (no administrator rights needed), plus
# installer\bin\BingLan-<version>-portable.zip: the same publish as an unpack-and-run archive.
param([string]$Version = "0.2.13")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $PSScriptRoot "obj\publish"
$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "未找到 Inno Setup 6：winget install JRSoftware.InnoSetup --scope user" }

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish (Join-Path $root "src\BingLan.App\BingLan.App.csproj") -c Release -r win-x64 `
    --self-contained true -p:SatelliteResourceLanguages="zh-Hans%3Ben" -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot "BingLan.iss")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Portable zip: the publish output plus the files the installer adds to {app}, under a
# single BingLan\ root so extraction does not spill files. .NET's ZipFile writes forward-
# slash entry names, which every unzipper reads correctly.
$portable = Join-Path $PSScriptRoot "obj\portable"
$stage = Join-Path $portable "BingLan"
if (Test-Path $portable) { Remove-Item -Recurse -Force $portable }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -Path (Join-Path $publish "*") -Destination $stage -Recurse
Copy-Item (Join-Path $root "docs\USER-GUIDE.md") (Join-Path $stage "使用说明.md")
New-Item -ItemType Directory -Path (Join-Path $stage "字体许可") | Out-Null
Copy-Item (Join-Path $root "src\BingLan.App\Assets\Fonts\LICENSE-*.txt") (Join-Path $stage "字体许可")
$zip = Join-Path $PSScriptRoot "bin\BingLan-$Version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
Remove-Item -Recurse -Force $portable
Write-Host "已生成 installer\bin\BingLan-Setup-$Version.exe 和 installer\bin\BingLan-$Version-portable.zip"
exit $LASTEXITCODE
