[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework 4.x compiler was not found.'
}
$outputDirectory = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$sources = @('Core.cs', 'Runtime.cs', 'NativeHeader.cs', 'Theme.cs', 'HeaderColorDialog.cs', 'Program.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Runtime.Serialization.dll', '/r:System.Web.Extensions.dll')
$iconPath = Join-Path $PSScriptRoot 'assets\app.ico'
$workspace = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$buildDirectory = Join-Path $workspace ('.build-' + [Guid]::NewGuid().ToString('N'))
try {
    & (Join-Path $PSScriptRoot 'tools\build-native.ps1') -OutputDirectory $buildDirectory
    $nativeHelper = Join-Path $buildDirectory 'HeaderHook.dll'
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /utf8output "/win32icon:$iconPath" "/resource:$iconPath,VkWorkspaceSwitcher.AppIcon" "/resource:$nativeHelper,VkWorkspaceSwitcher.HeaderHook" "/out:$(Join-Path $outputDirectory 'VKWorkspaceSwitcher.exe')" $references $sources
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
finally {
    $cleanupPath = [IO.Path]::GetFullPath($buildDirectory)
    if ((Split-Path -Parent $cleanupPath) -ne $workspace -or (Split-Path -Leaf $cleanupPath) -notmatch '^\.build-[0-9a-f]{32}$') {
        throw 'Unexpected temporary build path; cleanup cancelled.'
    }
    if (Test-Path -LiteralPath $cleanupPath) {
        $resolvedCleanup = (Resolve-Path -LiteralPath $cleanupPath).Path
        if ($resolvedCleanup -ne $cleanupPath -or (((Get-Item -LiteralPath $cleanupPath).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw 'Temporary build path is a link; cleanup cancelled.'
        }
        Remove-Item -LiteralPath $cleanupPath -Recurse -Force
    }
}
Write-Output (Join-Path $outputDirectory 'VKWorkspaceSwitcher.exe')
