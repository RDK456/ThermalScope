# ThermalScope

A Windows desktop app that does two jobs in one window:

- **Monitor** – CPU and GPU temperature, load, clock and power, shown on heat-ramp dials with a thermal scale, history and a live sensor list.
- **Frame pacer** – limits a game's frame rate with evenly spaced frames, with a live FPS / frame-time readout, a frame-time graph, per-game memory, hotkeys, a tray icon and an in-game overlay. (Inspired by the idea behind [framepacer](https://framepacer.com/); this is an independent implementation.)

It is self-contained: nothing here needs MSI Afterburner, RTSS or any other tool.

## Requirements

- Windows 10 or 11 (Windows 11 22H2+ for the Glass look's acrylic backdrop)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from source
- No administrator rights are needed to start the app

## Install

Download `ThermalScope-Setup-<version>.exe` from the [latest release](https://github.com/RDK456/ThermalScope/releases/latest) and run it. It installs per user to `%LOCALAPPDATA%\Programs\ThermalScope` (no administrator rights), adds a Start menu shortcut and an entry in *Apps > Installed apps*, and starts the app. The installer is unsigned, so Windows SmartScreen may ask you to confirm. Prefer no installer? Unzip `ThermalScope-<version>-win-x64.zip` anywhere.

### Updates

The app checks GitHub Releases at start (at most every 12 hours; switch off in Settings > General) and shows an **Update** button when a newer version exists. If the release carries a *delta* package for your exact version, only the changed files are downloaded (typically under 1 MB instead of ~68 MB); otherwise the full package is. The download is verified against the release's `SHA256SUMS.txt`, the app exits, `apply-update.ps1` swaps the files (backing them up and rolling back on any failure), and ThermalScope restarts. Copies that were not installed with Setup open the release page instead.

### Uninstall

*Apps > Installed apps > ThermalScope*, or run `uninstall.ps1` from the install folder (`-RemoveSettings` also deletes your settings).

### Releasing (maintainers)

```powershell
# bump <Version> in ThermalScope.csproj, then:
gh release download v1.1.0 --pattern manifest-1.1.0.json     # previous release's manifest
.\tools\release.ps1 -PreviousManifest .\manifest-1.1.0.json   # builds dist\ incl. the delta
gh release create v1.2.0 dist\* --title "ThermalScope 1.2.0" --notes-file notes.md
```

`dist\` gets the full zip, the delta zip, `ThermalScope-Setup-<v>.exe`, `manifest-<v>.json` (needed to build the *next* delta) and `SHA256SUMS.txt`. Upload all of them.

## Run

```powershell
.\run.ps1
```

or `dotnet run --project .\ThermalScope.csproj`. For a self-contained build:

```powershell
dotnet publish .\ThermalScope.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

## CPU temperature, honestly

Windows has no driver-free API for the CPU **die** temperature. The only thing it exposes without a driver is an ACPI thermal zone, which on a desktop is a motherboard/ambient reading, so ThermalScope never shows it as the CPU temperature. What the app does:

| Source | Needs | What you get |
| --- | --- | --- |
| Windows counters (always) | nothing | CPU load, **clock** (Processor Performance x base frequency) and **package power** (RAPL "Energy Meter", where the platform exposes it) |
| [PawnIO](https://github.com/namazso/PawnIO.Setup) driver + LibreHardwareMonitor | one-time install (button in the CPU card), then run as administrator | real CPU die/package/core temperatures |
| MSI Afterburner running (optional) | Afterburner | its CPU die temperature, read from `MAHMSharedMemory` |
| none of the above | – | the CPU card says so and shows what to do; GPU temperature, load, clock and power still work |

GPU values come from the graphics driver (NVIDIA/AMD/Intel) through LibreHardwareMonitor and need no extra driver.

## Frame pacer

1. Start a game in **borderless fullscreen**. ThermalScope detects it (foreground, fills its monitor, loads Direct3D) or choose it yourself with **Choose game…**.
2. Allow the one-time prompt to attach the helper.
3. Pick a target (presets, +/-, or type a number and press Apply). Changes apply to the running game immediately and are remembered per game.

The helper hooks `Present` (DXGI for DirectX 10/11/12, `IDirect3DDevice9::Present` for DirectX 9), waits before each present so frames arrive at an even cadence (high-resolution timer plus a short spin), and publishes FPS, frame time and a 240-frame ring through shared memory. A 64-bit helper (`native/ThermalScopeHook.dll`) and a 32-bit one (`ThermalScopeHook32.dll`) are embedded; the right one is injected after checking the game's bitness.

**Pacing modes** (Frame pacer > Pacing): *Precise timer* spaces frames evenly at any target. *Locked to refresh* is the console-style option: when the target divides your refresh rate (60 or 30 on 60 Hz, 72 on 144 Hz ...) the display's vertical blank sets the pace, so every frame stays up for exactly the same number of refreshes. The *Jitter* readout shows how much frame times vary (lower is smoother).

Supported: 32-bit and 64-bit DirectX 9/10/11/12 games (Locked-to-refresh needs DirectX 10/11/12). Not supported: Vulkan, OpenGL, DirectX 8, NVIDIA Reflex/DLSS frame-generation limiting. Games protected by Easy Anti-Cheat, BattlEye, Vanguard and similar are skipped on purpose; use the Xbox Game Bar counter there. Setup tips are in the app (fixed refresh: V-Sync on; G-Sync/FreeSync: limit a few FPS under the refresh rate).

### Overlay

A rounded, click-through strip at the top centre **inside the game window**: FPS with the low/high range, a frame-time line, and CPU/GPU load and temperature. It follows the window if the game moves or resizes, scales with the game window (Auto) or at a fixed 80-170%, never grows wider than the window, and only shows while the game is in front. Toggle it in Settings or with its hotkey. Exclusive-fullscreen games hide any window-based overlay.

### Hotkeys

System-wide, configurable in **Settings > Hotkeys** (click a shortcut, press the new keys; Esc cancels, Backspace clears; a modifier is required; clashes are reported). Defaults: Ctrl+Alt+F10 pause/resume limiter, Ctrl+Alt+PgUp / PgDn limit +/-5, Ctrl+Alt+T open the window, Ctrl+Alt+O overlay.

### Tray and startup

Close-to-tray and Start with Windows (silent, into the tray) are in Settings > General.

## Looks and scaling

**Settings > Look** has independent choices:

- **Style** – Scope (viewfinder corner marks, dot grid), Glass (acrylic backdrop), Depth (raised panels, soft shadows), Flat.
- **Appearance** – System, Dark, Light.
- **Accent colour** – six accents (temperature colours always keep their meaning).
- **Temperature dial** – Heat ramp, Zones, Minimal.
- **Interface size** – Auto (scales with the window, from small windows to 4K) or fixed 90-150%.

Not included yet: translation to other languages.

## Safety notes

- The helper is only loaded into a process you chose, after a one-time consent, and never into Windows system processes or anti-cheat protected games.
- The helper DLL is extracted to a hash-named file (an admin-only-writable folder when elevated) and re-verified by SHA-256 while locked at injection time.
- The PawnIO setup script is passed to an elevated PowerShell on the command line (no temp file to swap) and its download is pinned by SHA-256.

## Build the native helper

```powershell
.\native\build-native.ps1
```

Needs Visual Studio Build Tools with the C++ workload and the Windows SDK; MinHook 1.3.4 is cloned automatically. The rebuilt DLL is embedded in the next build.

## Project layout

- `MainWindow.xaml(.cs)` – header, Monitor view, Frame pacer view, Settings
- `OverlayWindow.xaml(.cs)` – in-game overlay
- `ViewModels/` – display state (cards, sensors, pacer, heat colours)
- `Controls/` – `ArcGauge`, `Sparkline`, `ThermalRuler`, `Card`
- `Themes/Theme.xaml` – control styles; `Services/LookManager.cs` builds colours and looks at runtime
- `Services/HardwareMonitorService.cs` – sensors and Windows-counter fallbacks; `AfterburnerReader.cs` optional source
- `Services/GameMonitorService.cs`, `GameFpsMonitor.cs`, `GameDetector.cs`, `AntiCheatGuard.cs`, `HookFileStore.cs`, `NativeProcessInjector.cs`, `SecureSharedMemory.cs` – pacer plumbing
- `Services/HotkeyManager.cs`, `TrayIcon.cs`, `StartupManager.cs`, `ThemeManager.cs`, `AppSettings.cs`
- `native/` – the Present hook (`ThermalScopeHook.cpp`, built x64 and x86 DLLs, build script)

Settings are stored in `%LOCALAPPDATA%\ThermalScope\` (`settings.json`, `game-monitor.json`, `thermal-scope.log`).
