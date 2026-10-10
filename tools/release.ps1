#requires -Version 5.1
<#
.SYNOPSIS
  Builds the release artefacts into dist\: full zip, delta zip, Setup.exe, manifest and SHA256SUMS.txt.

.PARAMETER PreviousManifest
  manifest.json of the previous release. When given, a delta package (only changed files) is produced.
  Download it with:  gh release download v1.0.0 --pattern manifest-1.0.0.json
#>
param(
    [string]$PreviousManifest
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root 'ThermalScope.csproj'
$version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in ThermalScope.csproj.' }
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

Write-Host "Publishing ThermalScope $version ..."
dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $stage -p:DebugType=none -p:DebugSymbols=false | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# ---- manifest ---------------------------------------------------------------------------------
function New-Manifest([string]$directory, [string]$ver) {
    $base = (Resolve-Path $directory).Path.TrimEnd('\').Length + 1
    $files = Get-ChildItem $directory -Recurse -File | Where-Object { $_.Name -ne 'manifest.json' } | ForEach-Object {
        [pscustomobject]@{
            path = $_.FullName.Substring($base) -replace '\\', '/'
            sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
            size = $_.Length
        }
    } | Sort-Object path
    [pscustomobject]@{ version = $ver; files = @($files) }
}
$manifest = New-Manifest $stage $version
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $stage 'manifest.json') -Encoding UTF8
Copy-Item (Join-Path $stage 'manifest.json') (Join-Path $dist "manifest-$version.json")

# ---- full package -----------------------------------------------------------------------------
$full = Join-Path $dist "ThermalScope-$version-win-x64.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $full, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host ("Full package : {0:N1} MB" -f ((Get-Item $full).Length / 1MB))

# ---- delta package ----------------------------------------------------------------------------
$delta = $null
if ($PreviousManifest) {
    $previous = Get-Content $PreviousManifest -Raw | ConvertFrom-Json
    $old = @{}; foreach ($f in $previous.files) { $old[$f.path] = $f.sha256 }
    $new = @{}; foreach ($f in $manifest.files) { $new[$f.path] = $f.sha256 }
    $changed = @($manifest.files | Where-Object { -not $old.ContainsKey($_.path) -or $old[$_.path] -ne $_.sha256 })
    $removed = @($previous.files | Where-Object { -not $new.ContainsKey($_.path) } | ForEach-Object { $_.path })

    $deltaStage = Join-Path $dist 'delta-stage'
    New-Item -ItemType Directory -Force (Join-Path $deltaStage 'files') | Out-Null
    foreach ($f in $changed) {
        $target = Join-Path (Join-Path $deltaStage 'files') ($f.path -replace '/', '\')
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item (Join-Path $stage ($f.path -replace '/', '\')) $target
    }
    [pscustomobject]@{ from = $previous.version; to = $version; remove = $removed } |
        ConvertTo-Json | Set-Content (Join-Path $deltaStage 'delta.json') -Encoding UTF8
    Copy-Item (Join-Path $stage 'manifest.json') (Join-Path $deltaStage 'manifest.json')
    $delta = Join-Path $dist "ThermalScope-$($previous.version)-to-$version.delta.zip"
    [System.IO.Compression.ZipFile]::CreateFromDirectory($deltaStage, $delta, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item $deltaStage -Recurse -Force
    Write-Host ("Delta package: {0:N1} MB ({1} changed, {2} removed)" -f ((Get-Item $delta).Length / 1MB), $changed.Count, $removed.Count)
}

# ---- Setup.exe (IExpress ships with Windows) --------------------------------------------------
$setupDir = Join-Path $dist 'setup-stage'
New-Item -ItemType Directory -Force $setupDir | Out-Null
Copy-Item $full (Join-Path $setupDir 'payload.zip')
Copy-Item (Join-Path $PSScriptRoot 'install.ps1') $setupDir
$setup = Join-Path $dist "ThermalScope-Setup-$version.exe"
$sed = Join-Path $setupDir 'setup.sed'
@"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgressWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$setup
FriendlyName=ThermalScope Setup $version
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File install.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[Strings]
[SourceFiles]
SourceFiles0=$setupDir\
[SourceFiles0]
install.ps1=
payload.zip=
"@ | Set-Content $sed -Encoding ASCII
& "$env:SystemRoot\System32\iexpress.exe" /N /Q $sed | Out-Null
$deadline = (Get-Date).AddMinutes(5)
while (-not (Test-Path $setup) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
if (-not (Test-Path $setup)) { throw 'IExpress did not produce the Setup executable.' }
while ($true) { try { [IO.File]::OpenRead($setup).Dispose(); break } catch { Start-Sleep -Milliseconds 500 } }  # wait for the write to finish
Write-Host ("Setup        : {0:N1} MB" -f ((Get-Item $setup).Length / 1MB))

# ---- checksums --------------------------------------------------------------------------------
$assets = @($full, $setup) + @($delta | Where-Object { $_ })
$sums = $assets | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_ -Algorithm SHA256).Hash, (Split-Path $_ -Leaf) }
Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Value $sums -Encoding ASCII

Remove-Item $stage, $setupDir -Recurse -Force
Write-Host "Done. Artefacts in $dist"
Get-ChildItem $dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table | Out-Host
