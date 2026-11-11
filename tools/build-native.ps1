[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ Build Tools are required for the Qt header helper.' }
$installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Install the Visual Studio C++ desktop build tools.' }
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
$nativeOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $nativeOutput -Force | Out-Null
$source = Join-Path $repo 'native\HeaderHook.cpp'
$dll = Join-Path $nativeOutput 'HeaderHook.dll'
$object = Join-Path $nativeOutput 'HeaderHook.obj'
$importLibrary = Join-Path $nativeOutput 'HeaderHook.lib'
$command = 'call "{0}" >nul && cl.exe /nologo /LD /MT /O2 /EHsc /std:c++17 /W4 /utf-8 /Fo"{1}" /Fe"{2}" "{3}" /link user32.lib /IMPLIB:"{4}"' -f $vcvars,$object,$dll,$source,$importLibrary
& $env:COMSPEC /d /c $command
if ($LASTEXITCODE -ne 0) { throw 'Native header helper build failed.' }
Write-Output $dll
