$ErrorActionPreference = 'Stop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'The .NET 8 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and try again.'
    exit 1
}

dotnet run --project (Join-Path $PSScriptRoot 'ThermalScope.csproj') @args
