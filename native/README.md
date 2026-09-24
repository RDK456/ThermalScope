# Native DirectX FPS monitor helper

`ThermalScopeHook.dll` is the small x64 helper extracted and attached by the WPF app when the user enables FPS monitoring. It uses MinHook to observe the DXGI `Present` path and a small shared-memory configuration block for the target process. It does not alter the game's frame rate.

The helper is intentionally limited to user-selected, 64-bit DirectX 11/12 processes. It does not bypass anti-cheat or protected-process security. The app disables FPS reporting when the manager exits; the helper remains loaded until the target process exits so active frames cannot race an unload.

The checked-in DLL is built from `ThermalScopeHook.cpp` with MinHook 1.3.4. To rebuild it on another Windows x64 development machine, obtain the MinHook 1.3.4 source and an LLVM/MinGW x64 toolchain, then run `build-native.ps1` with `-Toolchain` and `-MinHookSource`. The helper hooks both DXGI `Present` and `Present1` and falls back from a temporary D3D11 swap chain to D3D12 when needed.

Do not use this helper with anti-cheat protected games unless you have explicit permission to modify the process.
