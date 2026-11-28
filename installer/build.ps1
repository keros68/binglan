# Builds the self-contained installer and portable archive, or the framework-dependent archive.
param(
    [string]$Version = "0.7.6",
    [switch]$FrameworkDependentPortable
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$guide = Join-Path $root "docs\USER-GUIDE.md"
$fontLicenses = @(Get-ChildItem -Path (Join-Path $root "src\BingLan.App\Assets\Fonts\LICENSE-*.txt") -File)
$selfContained = -not $FrameworkDependentPortable
$variant = if ($selfContained) { "" } else { "-framework-dependent" }
if ($selfContained) {
    $iscc = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source,
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw "未找到 Inno Setup 6：winget install JRSoftware.InnoSetup --scope user" }
}

$publish = Join-Path $PSScriptRoot "obj\publish$variant"
if (Test-Path -LiteralPath $publish) { Remove-Item -Recurse -Force -LiteralPath $publish }
dotnet publish (Join-Path $root "src\BingLan.App\BingLan.App.csproj") -c Release -r win-x64 `
    --self-contained $selfContained.ToString().ToLowerInvariant() -p:SatelliteResourceLanguages="zh-Hans%3Ben" -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($fileName in @("BingLan.exe", "BingLan.Composition.dll", "BingLan.Taskbar.Xaml.dll", "BingLan.TrayContextNative.dll")) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $fileName) -PathType Leaf)) {
        throw "发布目录缺少 $fileName。"
    }
}
if ($selfContained) {
    $packageHashes = & {
        foreach ($file in (Get-ChildItem -LiteralPath $publish -File -Recurse)) {
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            $name = [IO.Path]::GetRelativePath($publish, $file.FullName)
            "$hash  $name"
        }
        foreach ($file in $fontLicenses) {
            "$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)  字体许可\$($file.Name)"
        }
        "$((Get-FileHash -LiteralPath $guide -Algorithm SHA256).Hash)  使用说明.md"
    }
    $packageHashes | Set-Content -LiteralPath (Join-Path $PSScriptRoot "obj\publish-sha256.txt") -Encoding utf8BOM

    & $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot "BingLan.iss")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Keep the publish output and the additional documentation under a single BingLan root.
$portable = Join-Path $PSScriptRoot "obj\portable$variant"
$stage = Join-Path $portable "BingLan"
if (Test-Path -LiteralPath $portable) { Remove-Item -Recurse -Force -LiteralPath $portable }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -Path (Join-Path $publish "*") -Destination $stage -Recurse
if ($FrameworkDependentPortable) {
    Get-ChildItem -LiteralPath $stage -File -Recurse -Filter "*.pdb" | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName
    }
}
Copy-Item -LiteralPath $guide -Destination (Join-Path $stage "使用说明.md")
New-Item -ItemType Directory -Path (Join-Path $stage "字体许可") | Out-Null
Copy-Item -LiteralPath $fontLicenses.FullName -Destination (Join-Path $stage "字体许可")
$outputFolder = Join-Path $PSScriptRoot "bin"
New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null
$zipName = "BingLan-$Version-portable$variant.zip"
$zip = Join-Path $outputFolder $zipName
$temporaryZip = Join-Path $outputFolder ([guid]::NewGuid().ToString() + ".tmp")
try {
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stage, $temporaryZip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Move-Item -Force -LiteralPath $temporaryZip -Destination $zip
} finally {
    if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip }
    Remove-Item -Recurse -Force -LiteralPath $portable
}
if ($selfContained) {
    Write-Host "已生成 installer\bin\BingLan-Setup-$Version.exe 和 installer\bin\$zipName"
} else {
    Write-Host "已生成 installer\bin\$zipName（需要 .NET 10 Windows Desktop Runtime x64）"
}
