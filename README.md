# ThermalScope

ThermalScope is a native Windows desktop dashboard for watching CPU and GPU activity in real time. It shows:

- CPU and GPU utilization
- CPU package and GPU core temperatures when the hardware exposes them
- CPU/GPU clocks and power when reported by the sensor driver
- A live list of detected temperature, load, clock, and power sensors, including unavailable readings
- Game process monitoring with a built-in DirectX 11/12 FPS readout
- Adjustable refresh intervals and a manual refresh action

The app uses [LibreHardwareMonitorLib](https://www.nuget.org/packages/LibreHardwareMonitorLib/) for hardware sensor access and Windows performance counters as a usage fallback. LibreHardwareMonitorLib is MPL-2.0 licensed; review its license and the transitive package notices before redistributing a packaged build.

## Requirements

- Windows 10 or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) for building from source
- Administrator access is required for the built-in FPS monitor and recommended for complete sensor coverage
- A 64-bit DirectX 11/12 game for the built-in FPS monitor

## Run

From PowerShell in this folder:

```powershell
.\run.ps1
```

Or build and run directly:

```powershell
dotnet restore
dotnet run --project .\ThermalScope.csproj
```

To start with elevated sensor access:

```powershell
.\run-elevated.ps1
```

If CPU temperature sensors are detected but show `—`, install the official PawnIO driver and then restart the app:

```powershell
.\install-pawnio.ps1
```

On the first launch, ThermalScope detects missing PawnIO and asks once whether to open the official installer. It does not silently install a kernel driver: Windows shows its normal administrator/UAC confirmation. You can also retry from the **Set up CPU sensors** button in the app.

The app now keeps detected-but-unavailable CPU sensors in the live sensor list, so you can see whether the CPU exposes package/core/clock/power sensors even when the driver cannot read their values.

For a self-contained x64 build (no .NET runtime required on the target machine):

```powershell
dotnet restore .\ThermalScope.csproj -r win-x64
dotnet publish .\ThermalScope.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

For one self-contained executable:

```powershell
dotnet publish .\ThermalScope.csproj -c Release -r win-x64 --self-contained true -o .\publish-single -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## Notes

Temperature availability is hardware- and driver-dependent. A value shown as `—` means that Windows or the sensor driver did not report a usable reading; it does not mean the component is at zero degrees. Some laptop and desktop GPUs expose temperature only through vendor-specific drivers.

LibreHardwareMonitor 0.9.6 uses the PawnIO kernel driver for some motherboard and CPU sensors. The NuGet library does not install that driver automatically. If ThermalScope reports that PawnIO is unavailable, install it from the [official PawnIO releases](https://github.com/namazso/PawnIO.Setup/releases), then restart ThermalScope. Do not disable antivirus protection or install unsigned drivers.

The first sample can take a moment while hardware providers are initialized. The refresh interval cycles through 1, 2, 5, and 10 seconds.

## Game FPS control

The **Game FPS** card below the GPU card watches visible Windows processes and displays the current frame rate for the selected DirectX game.

1. Select the actual rendering executable from **Visible processes** (not only a launcher), or type its executable name.
2. Leave **Monitor automatically while the selected game is running** enabled.
3. Click **Monitor FPS**.

The monitor is built into ThermalScope and does not require RTSS or MSI Afterburner. It extracts a small native x64 helper, attaches it to the selected process, and observes the DXGI `Present` path for DirectX 11/12 without changing the game's frame rate. The monitor detaches when the game closes or ThermalScope exits, and the game process owns the helper until it exits.

**Important:** this is an opt-in, read-only process attach for FPS monitoring. Run ThermalScope as administrator, and only use it with software where you are allowed to inject a helper. Anti-cheat protected games may block or terminate the operation; ThermalScope does not bypass anti-cheat. The first version supports 64-bit DirectX 11/12 processes only. Current FPS is reported when the game is rendering through the hooked swap chain; VSync and fullscreen optimizations can affect the observed value. The selected executable and auto-monitor preference are stored locally in `%LOCALAPPDATA%\ThermalScope\game-monitor.json`.

## Project layout

- `MainWindow.xaml` / `MainWindow.xaml.cs` — dashboard UI and live rendering
- `Services/HardwareMonitorService.cs` — background polling, sensor selection, and Windows counter fallback
- `Services/GameMonitorService.cs` — process monitoring and automatic FPS-monitor state
- `Services/GameFpsMonitor.cs` — built-in shared-memory configuration and native helper lifecycle
- `Services/NativeProcessInjector.cs` — opt-in x64 helper attachment
- `native/ThermalScopeHook.cpp` — native DXGI Present/Present1 FPS hook implementation
- `native/build-native.ps1` — optional native helper rebuild script
- `native/ThermalScopeHook.dll` — checked-in x64 helper embedded into the executable
- `native/MinHook-LICENSE.txt` — MinHook BSD-2-Clause notice
- `Services/PawnIoSetupManager.cs` — one-time consent, official installer launch, and restart handling
- `Models/MonitorSnapshot.cs` — immutable CPU/GPU data passed from the polling service to the UI
- `Models/GameMonitorSnapshot.cs` — game process and FPS-monitor state passed to the UI
- `install-pawnio.ps1` — optional official PawnIO setup for CPU sensor access
- `app.manifest` — Windows compatibility and per-monitor DPI settings
