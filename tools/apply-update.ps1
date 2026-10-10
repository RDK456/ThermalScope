#requires -Version 5.1
<#
  Applies an update package to an installed ThermalScope after the app has exited.
  Full package : app files at the zip root + manifest.json
  Delta package: delta.json (from/to/remove), manifest.json and files\<changed files>
  Files are backed up first; any failure restores them and restarts the old version.
#>
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$InstallDir,
    [Parameter(Mandatory)][string]$Package,
    [string]$Exe = 'ThermalScope.exe'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$logDir = Join-Path $env:LOCALAPPDATA 'ThermalScope'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir 'update.log'
function Log([string]$message) { Add-Content -Path $log -Value ('[{0:u}] {1}' -f (Get-Date), $message) }

$work = Join-Path $env:TEMP ('ThermalScope-update-' + [guid]::NewGuid().ToString('N'))
$backup = Join-Path $work 'backup'
$staged = Join-Path $work 'pkg'
$touched = New-Object System.Collections.Generic.List[string]   # "relative|existedBefore"
$removed = New-Object System.Collections.Generic.List[string]   # relative paths we deleted (backed up)

function Copy-WithRetry([string]$from, [string]$to) {
    for ($i = 0; $i -lt 40; $i++) {
        try { Copy-Item -LiteralPath $from -Destination $to -Force; return }
        catch { if ($i -eq 39) { throw }; Start-Sleep -Milliseconds 500 }
    }
}

function Backup-File([string]$relative) {
    $target = Join-Path $InstallDir $relative
    if (Test-Path -LiteralPath $target) {
        $copy = Join-Path $backup $relative
        New-Item -ItemType Directory -Force (Split-Path $copy) | Out-Null
        Copy-WithRetry $target $copy
        return $true
    }
    return $false
}

try {
    Log "Updating $InstallDir from $Package"
    Get-Process -Id $ProcessId -ErrorAction SilentlyContinue | Wait-Process -Timeout 45 -ErrorAction SilentlyContinue

    New-Item -ItemType Directory -Force $backup, $staged | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($Package, $staged)

    $manifestPath = Join-Path $staged 'manifest.json'
    if (-not (Test-Path $manifestPath)) { throw 'The package has no manifest.json.' }
    $newManifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

    $deltaPath = Join-Path $staged 'delta.json'
    $isDelta = Test-Path $deltaPath
    $sourceRoot = if ($isDelta) { Join-Path $staged 'files' } else { $staged }

    # Which files are written, which are deleted.
    $writes = @()
    if (Test-Path $sourceRoot) {
        $rootLength = (Resolve-Path $sourceRoot).Path.TrimEnd('\').Length + 1
        $writes = Get-ChildItem -Path $sourceRoot -Recurse -File | ForEach-Object { $_.FullName.Substring($rootLength) }
    }
    $writes = @($writes | Where-Object { $_ -ne 'manifest.json' -and $_ -ne 'delta.json' })

    $deletes = @()
    if ($isDelta) {
        $deletes = @((Get-Content $deltaPath -Raw | ConvertFrom-Json).remove)
    } else {
        $oldManifestPath = Join-Path $InstallDir 'manifest.json'
        if (Test-Path $oldManifestPath) {
            $old = (Get-Content $oldManifestPath -Raw | ConvertFrom-Json).files | ForEach-Object { $_.path }
            $new = $newManifest.files | ForEach-Object { $_.path }
            $deletes = @($old | Where-Object { $new -notcontains $_ })
        }
    }

    foreach ($relative in $writes) {
        $existed = Backup-File $relative
        $touched.Add("$relative|$existed")
        $dest = Join-Path $InstallDir $relative
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-WithRetry (Join-Path $sourceRoot $relative) $dest
    }
    foreach ($relative in $deletes) {
        $native = $relative -replace '/', '\'
        if (Backup-File $native) {
            $removed.Add($native)
            Remove-Item -LiteralPath (Join-Path $InstallDir $native) -Force
        }
    }
    Copy-WithRetry $manifestPath (Join-Path $InstallDir 'manifest.json')

    # Verify what we wrote against the manifest.
    $expected = @{}
    foreach ($file in $newManifest.files) { $expected[$file.path] = $file.sha256 }
    foreach ($relative in $writes) {
        $key = $relative -replace '\\', '/'
        if ($expected.ContainsKey($key)) {
            $hash = (Get-FileHash -LiteralPath (Join-Path $InstallDir $relative) -Algorithm SHA256).Hash
            if ($hash -ne $expected[$key]) { throw "Checksum mismatch after copying $relative." }
        }
    }

    Log "Updated to $($newManifest.version) ($($writes.Count) files written, $($deletes.Count) removed, delta=$isDelta)"
}
catch {
    Log "Update failed: $($_.Exception.Message). Rolling back."
    try {
        foreach ($entry in $touched) {
            $relative, $existed = $entry -split '\|'
            $dest = Join-Path $InstallDir $relative
            if ($existed -eq 'True') { Copy-Item -LiteralPath (Join-Path $backup $relative) -Destination $dest -Force }
            elseif (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Force }
        }
        foreach ($relative in $removed) {
            Copy-Item -LiteralPath (Join-Path $backup $relative) -Destination (Join-Path $InstallDir $relative) -Force
        }
        Log 'Rollback complete.'
    } catch { Log "Rollback failed: $($_.Exception.Message)" }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $Package -Force -ErrorAction SilentlyContinue
    $app = Join-Path $InstallDir $Exe
    if (Test-Path $app) { Start-Process -FilePath $app -WorkingDirectory $InstallDir }
}
