#requires -Version 5.1
# Removes ThermalScope. Your settings in %LOCALAPPDATA%\ThermalScope are kept unless you pass -RemoveSettings.
param([switch]$RemoveSettings)
$ErrorActionPreference = 'SilentlyContinue'
$target = $PSScriptRoot

Get-Process ThermalScope | Stop-Process -Force
Start-Sleep -Milliseconds 600

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ThermalScope'
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ThermalScope' -Recurse -Force
Remove-Item -Path (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ThermalScope.lnk') -Force
if ($RemoveSettings) { Remove-Item -Path (Join-Path $env:LOCALAPPDATA 'ThermalScope') -Recurse -Force }

# This script lives inside the folder it deletes: hand the removal to a detached cmd that waits for us to exit.
Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" -WindowStyle Hidden `
    -ArgumentList "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q `"$target`""
