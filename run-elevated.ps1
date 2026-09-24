$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'run.ps1'
Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', ('"{0}"' -f $scriptPath)
)
