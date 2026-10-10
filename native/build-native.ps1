#requires -Version 5.1
<#
.SYNOPSIS
  Rebuilds native\ThermalScopeHook.dll (x64) and native\ThermalScopeHook32.dll (x86) with MSVC and MinHook 1.3.4.

.DESCRIPTION
  Needs Visual Studio Build Tools with the "Desktop development with C++" workload
  (any 2019/2022 edition). MinHook is cloned on demand into native\minhook-1.3.4.
  The CRT is linked statically (/MT) so the DLLs load in games without a VC++ redistributable.
#>
param(
    [string]$MinHookSource = "$PSScriptRoot\minhook-1.3.4",
    [ValidateSet('x64', 'x86', 'both')][string]$Arch = 'both'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $MinHookSource 'include\MinHook.h'))) {
    Write-Host 'Fetching MinHook v1.3.4...'
    git clone --quiet --depth 1 --branch v1.3.4 https://github.com/TsudaKageyu/minhook.git $MinHookSource
    if ($LASTEXITCODE -ne 0) { throw 'Could not clone MinHook v1.3.4.' }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio Build Tools were not found (vswhere.exe is missing).' }
$installPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installPath) { throw 'Install the "Desktop development with C++" workload of Visual Studio Build Tools.' }

# Some Build Tools installs ship the Windows SDK without registering it, so vcvars misses it.
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdk = Get-ChildItem (Join-Path $kits 'Include') -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path (Join-Path $_.FullName 'um\d3d12.h') } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdk) { throw 'The Windows 10/11 SDK is missing. Add it to Visual Studio Build Tools.' }
$sdkInclude = ('ucrt', 'um', 'shared' | ForEach-Object { Join-Path $sdk.FullName $_ }) -join ';'

function Build-Hook([string]$target) {
    $x86 = $target -eq 'x86'
    $vcvars = Join-Path $installPath ('VC\Auxiliary\Build\' + $(if ($x86) { 'vcvars32.bat' } else { 'vcvars64.bat' }))
    $source = Join-Path $MinHookSource 'src'
    $sources = @(
        (Join-Path $source 'buffer.c'),
        (Join-Path $source 'hook.c'),
        (Join-Path $source 'trampoline.c'),
        (Join-Path $source ($(if ($x86) { 'hde\hde32.c' } else { 'hde\hde64.c' }))),
        (Join-Path $PSScriptRoot 'ThermalScopeHook.cpp')
    ) | ForEach-Object { '"{0}"' -f $_ }

    $output = Join-Path $PSScriptRoot ($(if ($x86) { 'ThermalScopeHook32.dll' } else { 'ThermalScopeHook.dll' }))
    $build = Join-Path $env:TEMP "ThermalScope-native-build-$target"
    Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $build | Out-Null

    $compile = 'cl /nologo /LD /MT /O2 /GS /W3 /std:c++17 /D_WIN32_WINNT=0x0A00 ' +
        ('/I"{0}" /I"{1}" ' -f (Join-Path $MinHookSource 'include'), $source) +
        ('/Fo"{0}\\" ' -f $build) + ('/Fe"{0}" ' -f $output) + ($sources -join ' ') +
        ' /link /DYNAMICBASE /NXCOMPAT /guard:cf d3d9.lib d3d11.lib d3d12.lib dxgi.lib ole32.lib user32.lib'

    $lib = ('ucrt', 'um' | ForEach-Object { Join-Path $kits "Lib\$($sdk.Name)\$_\$target" }) -join ';'
    $script = Join-Path $build 'build.cmd'
    Set-Content $script -Encoding ascii -Value @(
        '@echo off',
        ('set "PATH=%PATH%;{0}"' -f (Split-Path $vswhere)),
        ('call "{0}" >nul' -f $vcvars),
        ('set "INCLUDE=%INCLUDE%;{0}"' -f $sdkInclude),
        ('set "LIB=%LIB%;{0}"' -f $lib),
        $compile)
    & "$env:SystemRoot\System32\cmd.exe" /c $script
    if ($LASTEXITCODE -ne 0) { throw "cl.exe ($target) failed with exit code $LASTEXITCODE." }

    Write-Host "Built $output ($((Get-Item $output).Length) bytes)"
    Write-Host ('SHA-256: ' + (Get-FileHash $output -Algorithm SHA256).Hash)
}

if ($Arch -in 'x64', 'both') { Build-Hook 'x64' }
if ($Arch -in 'x86', 'both') { Build-Hook 'x86' }
