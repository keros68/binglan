param([ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version = "0.2.44")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$stage = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "obj\tray-preview"))
$expected = [IO.Path]::GetFullPath((Join-Path $root "installer\obj")) + [IO.Path]::DirectorySeparatorChar
if (-not $stage.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) {
    throw "验收包暂存目录超出 installer\obj"
}
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$publish = Join-Path $stage "BingLan-TrayPreview"
dotnet publish (Join-Path $root "tests\BingLan.UiSmokeTests\BingLan.UiSmokeTests.csproj") `
    -c Release -r win-x64 --self-contained true -p:SatelliteResourceLanguages="zh-Hans%3Ben" `
    -p:TrayCompatibilityPreview=true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# The apphost embeds its managed DLL path, so its executable name can be user-facing.
Move-Item -LiteralPath (Join-Path $publish "BingLan.UiSmokeTests.exe") `
    -Destination (Join-Path $publish "BingLan.TrayPreview.exe")
Remove-Item -LiteralPath (Join-Path $publish "BingLan.exe")
Copy-Item -LiteralPath (Join-Path $root "docs\TRAY-PREVIEW.md") -Destination (Join-Path $publish "托盘验收说明.md")
Copy-Item -LiteralPath (Join-Path $root "LICENSE"), (Join-Path $root "NOTICE.md") -Destination $publish
$bin = Join-Path $PSScriptRoot "bin"
New-Item -ItemType Directory -Path $bin -Force | Out-Null
$zip = Join-Path $bin "BingLan-$Version-tray-preview.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "已生成 $zip"
