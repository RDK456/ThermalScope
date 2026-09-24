param(
    [string]$Toolchain = $env:LLVM_MINGW_ROOT,
    [string]$MinHookSource = "$PSScriptRoot\minhook-1.3.4"
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Toolchain)) {
    $Toolchain = 'C:\path\to\llvm-mingw\bin'
}

$clang = Join-Path $Toolchain 'clang.exe'
$clangxx = Join-Path $Toolchain 'clang++.exe'
if (-not (Test-Path $clang) -or -not (Test-Path $clangxx)) {
    throw "Set LLVM_MINGW_ROOT to an llvm-mingw x64 bin directory."
}

$required = @(
    'include\MinHook.h',
    'src\buffer.c',
    'src\trampoline.c',
    'src\hook.c',
    'src\hde\hde64.c'
)
foreach ($relative in $required) {
    if (-not (Test-Path (Join-Path $MinHookSource $relative))) {
        throw "MinHook source is missing: $relative"
    }
}

$build = Join-Path $env:TEMP 'ThermalScope-native-build'
Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $build | Out-Null
$include = Join-Path $MinHookSource 'include'
$source = Join-Path $MinHookSource 'src'
$hde = Join-Path $source 'hde'
$common = @('-O2', '-D_WIN32_WINNT=0x0A00', '-I', $include, '-I', $source)

& $clang -c @common (Join-Path $source 'buffer.c') -o (Join-Path $build 'buffer.o')
& $clang -c @common (Join-Path $source 'trampoline.c') -o (Join-Path $build 'trampoline.o')
& $clang -c @common (Join-Path $source 'hook.c') -o (Join-Path $build 'hook.o')
& $clang -c @common '-I' $hde (Join-Path $hde 'hde64.c') -o (Join-Path $build 'hde64.o')
& $clangxx -c -O2 -std=c++17 -D_WIN32_WINNT=0x0A00 -I $include -I $PSScriptRoot (Join-Path $PSScriptRoot 'ThermalScopeHook.cpp') -o (Join-Path $build 'ThermalScopeHook.o')
& $clangxx -shared -O2 -o (Join-Path $PSScriptRoot 'ThermalScopeHook.dll') `
    (Join-Path $build 'buffer.o') `
    (Join-Path $build 'trampoline.o') `
    (Join-Path $build 'hook.o') `
    (Join-Path $build 'hde64.o') `
    (Join-Path $build 'ThermalScopeHook.o') `
    -ld3d11 -ld3d12 -ldxgi -lole32 -luser32

Write-Host "Built $PSScriptRoot\ThermalScopeHook.dll"
