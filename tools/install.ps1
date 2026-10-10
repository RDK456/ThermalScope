#requires -Version 5.1
# ThermalScope setup: per-user install (no administrator rights needed). Runs from the self-extracting Setup.exe.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$payload = Join-Path $PSScriptRoot 'payload.zip'
$target = Join-Path $env:LOCALAPPDATA 'Programs\ThermalScope'

$form = New-Object System.Windows.Forms.Form
$form.Text = 'ThermalScope Setup'
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false; $form.MinimizeBox = $false
$form.ClientSize = New-Object System.Drawing.Size(420, 110)
$form.TopMost = $true
$label = New-Object System.Windows.Forms.Label
$label.AutoSize = $false; $label.Location = '20,18'; $label.Size = '380,24'; $label.Text = 'Installing ThermalScope...'
$bar = New-Object System.Windows.Forms.ProgressBar
$bar.Style = 'Marquee'; $bar.MarqueeAnimationSpeed = 30; $bar.Location = '20,56'; $bar.Size = '380,18'
$form.Controls.AddRange(@($label, $bar))
$form.Show(); [System.Windows.Forms.Application]::DoEvents()

function Step([string]$text) { $label.Text = $text; [System.Windows.Forms.Application]::DoEvents() }

try {
    Step 'Closing any running copy...'
    Get-Process ThermalScope -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600

    Step "Copying files to $target ..."
    New-Item -ItemType Directory -Force $target | Out-Null
    $tar = Start-Process -FilePath "$env:SystemRoot\System32\tar.exe" -ArgumentList @('-xf', "`"$payload`"", '-C', "`"$target`"") -PassThru -WindowStyle Hidden
    while (-not $tar.HasExited) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 80 }
    if ($tar.ExitCode -ne 0) { throw "Extraction failed (tar exit code $($tar.ExitCode))." }

    $exe = Join-Path $target 'ThermalScope.exe'
    if (-not (Test-Path $exe)) { throw 'ThermalScope.exe was not found after extraction.' }
    $manifest = Get-Content (Join-Path $target 'manifest.json') -Raw | ConvertFrom-Json

    Step 'Creating shortcuts...'
    $shell = New-Object -ComObject WScript.Shell
    $programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $link = $shell.CreateShortcut((Join-Path $programs 'ThermalScope.lnk'))
    $link.TargetPath = $exe; $link.WorkingDirectory = $target; $link.IconLocation = $exe
    $link.Description = 'CPU/GPU monitor and frame pacer'
    $link.Save()

    Step 'Registering with Windows...'
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ThermalScope'
    New-Item -Path $key -Force | Out-Null
    $size = [int]((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    $values = @{
        DisplayName = 'ThermalScope'; DisplayVersion = $manifest.version; Publisher = 'RDK456'
        InstallLocation = $target; DisplayIcon = $exe; EstimatedSize = $size
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$target\uninstall.ps1`""
        URLInfoAbout = 'https://github.com/RDK456/ThermalScope'
    }
    foreach ($name in $values.Keys) { Set-ItemProperty -Path $key -Name $name -Value $values[$name] }
    Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $key -Name NoRepair -Value 1 -Type DWord

    $form.Close()
    Start-Process -FilePath $exe -WorkingDirectory $target
}
catch {
    $form.Close()
    [System.Windows.Forms.MessageBox]::Show("Setup failed:`n$($_.Exception.Message)", 'ThermalScope Setup', 'OK', 'Error') | Out-Null
    exit 1
}
