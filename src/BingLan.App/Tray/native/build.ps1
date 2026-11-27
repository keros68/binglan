param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\bin\Release\net10.0-windows')
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$intermediate = Join-Path $OutputDirectory 'native-obj'
New-Item -ItemType Directory -Path $intermediate -Force | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ Build Tools are required for the tray bridge.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'The x64 C++ toolchain is unavailable.' }
$vc = Get-ChildItem -LiteralPath (Join-Path $installation 'VC\Tools\MSVC') -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdkVersion = Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um\UIAutomationClient.h') } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
if (!$sdkVersion) { throw 'A Windows SDK with UI Automation headers is required.' }

$originalInclude = $env:INCLUDE
$originalLib = $env:LIB
try {
    $env:INCLUDE = "$vc\include;$sdk\Include\$sdkVersion\ucrt;$sdk\Include\$sdkVersion\shared;$sdk\Include\$sdkVersion\um;$sdk\Include\$sdkVersion\winrt"
    $env:LIB = "$vc\lib\x64;$sdk\Lib\$sdkVersion\ucrt\x64;$sdk\Lib\$sdkVersion\um\x64"
    $compiler = Join-Path $vc 'bin\Hostx64\x64\cl.exe'
    $source = Join-Path $PSScriptRoot 'TrayContextNative.cpp'
    $object = Join-Path $intermediate 'TrayContextNative.obj'
    $dll = Join-Path $OutputDirectory 'BingLan.TrayContextNative.dll'
    $importLibrary = Join-Path $intermediate 'BingLan.TrayContextNative.lib'
    & $compiler /nologo /EHsc /std:c++17 /W4 /O2 /MT /LD /DUNICODE /D_UNICODE $source "/Fo$object" /link "/OUT:$dll" "/IMPLIB:$importLibrary" ole32.lib oleaut32.lib user32.lib uiautomationcore.lib
    if ($LASTEXITCODE -ne 0) { throw "Native UIA bridge compilation failed ($LASTEXITCODE)." }
    Write-Output "Built x64 UI Automation tray bridge: $dll"
}
finally {
    $env:INCLUDE = $originalInclude
    $env:LIB = $originalLib
}
