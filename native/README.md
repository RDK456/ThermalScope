# Native frame-pacer helper

`ThermalScopeHook.dll` is the small x64 helper that ThermalScope loads into a game you chose. It uses MinHook to hook DXGI `Present` / `Present1`, then:

- waits before each present so frames are evenly spaced (the frame limiter; target set live through shared memory),
- publishes average FPS, slowest-frame FPS, frame time and a 240-frame ring buffer for the graph and overlay,
- follows a single swap chain (overlay swap chains are ignored) and reports 0 when the game stops presenting.

It is limited to user-selected, 64-bit DirectX 10/11/12 processes, is never attached to Windows processes or anti-cheat protected games, and stops limiting if ThermalScope exits. It stays loaded until the game exits so in-flight frames cannot race an unload.

The shared-memory layout (version 3) is documented at the top of `ThermalScopeHook.cpp` and mirrored in `Services/GameFpsMonitor.cs`.

## Rebuild

```powershell
.\build-native.ps1
```

Needs Visual Studio Build Tools (C++ workload) and the Windows SDK. MinHook 1.3.4 is cloned into `native\minhook-1.3.4` (git-ignored). The DLL is linked with a static CRT so it loads in games without a redistributable. The checked-in DLL is embedded into the app at build time.
