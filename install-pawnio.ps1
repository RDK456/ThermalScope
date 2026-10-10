$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$packageId = 'namazso.PawnIO'
$winget = Get-Command winget -ErrorAction SilentlyContinue

if ($winget) {
    Write-Host 'Opening the official PawnIO package installer through winget...'
    & $winget.Source install --id $packageId --exact --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -eq 0) {
        Write-Host 'PawnIO installation completed. Restart ThermalScope.'
    }
    exit $LASTEXITCODE
}

$version = '2.2.0'
$url = "https://github.com/namazso/PawnIO.Setup/releases/download/$version/PawnIO_setup.exe"
$installer = Join-Path $env:TEMP "PawnIO_setup-$version.exe"

Write-Host "Downloading the official PawnIO $version installer..."
Invoke-WebRequest -Uri $url -OutFile $installer

# SHA-256 of PawnIO_setup.exe 2.2.0 as published in the GitHub release metadata (namazso/PawnIO.Setup).
$expectedHash = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
$actualHash = (Get-FileHash -Path $installer -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) {
    Remove-Item $installer -Force -ErrorAction SilentlyContinue
    throw "The downloaded PawnIO installer does not match the expected SHA-256 ($actualHash). Installation was cancelled."
}

$signature = Get-AuthenticodeSignature -FilePath $installer
if ($signature.Status -ne 'Valid') {
    Remove-Item $installer -Force -ErrorAction SilentlyContinue
    throw "The downloaded PawnIO installer did not have a valid Authenticode signature. Installation was cancelled."
}

if ($isAdministrator) {
    Start-Process -FilePath $installer -Wait
}
else {
    Write-Host 'Starting the PawnIO installer. Accept the Windows administrator prompt to continue.'
    Start-Process -FilePath $installer -Verb RunAs -Wait
}
Write-Host 'PawnIO installer finished. Restart ThermalScope so it can reload the driver.'
