param([string]$OutputDirectory = "$PSScriptRoot\bin\Release")
$ErrorActionPreference = 'Stop'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ x64 build tools and Windows SDK are required.' }
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Push-Location (Resolve-Path -LiteralPath $OutputDirectory).Path
try {
    & cl.exe /nologo /std:c++20 /EHsc /W4 /WX /O2 /MT /DUNICODE /D_UNICODE /LD "$PSScriptRoot\BackdropComposition.cpp" /link /WX /OUT:BingLan.Composition.dll CoreMessaging.lib windowsapp.lib ole32.lib user32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Composition backdrop component build failed.' }
} finally { Pop-Location }
