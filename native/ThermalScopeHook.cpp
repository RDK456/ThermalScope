#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi.h>
#include <dxgi1_2.h>
#include <dxgi1_3.h>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include "MinHook.h"

namespace
{
constexpr uint32_t ConfigMagic = 0x54494D31; // "1MIT" in little-endian byte order.
constexpr uint32_t ConfigVersion = 1;
constexpr wchar_t ConfigMappingName[] = L"Local\\ThermalScopeFpsConfig";

struct SharedConfig
{
    uint32_t magic;
    uint32_t version;
    uint32_t controllerPid;
    uint32_t targetPid;
    uint32_t reservedFps;
    uint32_t enabled;
    uint32_t hookReady;
    uint32_t currentFpsBits;
    uint32_t reserved;
};

using PresentFunction = HRESULT(WINAPI*)(IDXGISwapChain*, UINT, UINT);
using Present1Function = HRESULT(WINAPI*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);

PresentFunction g_originalPresent = nullptr;
Present1Function g_originalPresent1 = nullptr;
std::atomic<bool> g_enabled{ false };
std::atomic<int64_t> g_lastPresent{ 0 };
std::atomic<float> g_currentFps{ 0.0f };
LARGE_INTEGER g_frequency{ };
HANDLE g_configMapping = nullptr;
void* g_configView = nullptr;

void WriteHookReady(bool ready)
{
    if (g_configView == nullptr)
    {
        return;
    }

    static_cast<SharedConfig*>(g_configView)->hookReady = ready ? 1u : 0u;
}

bool IsControllerAlive(uint32_t controllerPid)
{
    if (controllerPid == 0)
    {
        return false;
    }

    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, static_cast<DWORD>(controllerPid));
    if (process == nullptr)
    {
        return false;
    }

    const DWORD result = WaitForSingleObject(process, 0);
    CloseHandle(process);
    return result == WAIT_TIMEOUT;
}

void WriteCurrentFps()
{
    if (g_configView == nullptr)
    {
        return;
    }

    const float fps = g_enabled.load(std::memory_order_acquire)
        ? g_currentFps.load(std::memory_order_relaxed)
        : 0.0f;
    uint32_t bits = 0;
    std::memcpy(&bits, &fps, sizeof(bits));
    static_cast<SharedConfig*>(g_configView)->currentFpsBits = bits;
}

void RefreshConfig()
{
    if (g_configView == nullptr)
    {
        g_enabled.store(false, std::memory_order_release);
        return;
    }

    const auto* config = static_cast<const SharedConfig*>(g_configView);
    const bool valid = config->magic == ConfigMagic &&
        config->version == ConfigVersion &&
        config->targetPid == GetCurrentProcessId() &&
        config->enabled != 0 &&
        IsControllerAlive(config->controllerPid);

    g_enabled.store(valid, std::memory_order_release);
    WriteCurrentFps();
}

void RecordFps()
{
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    const int64_t previous = g_lastPresent.exchange(now.QuadPart, std::memory_order_relaxed);
    if (previous == 0 || g_frequency.QuadPart <= 0)
    {
        return;
    }

    const double elapsed = static_cast<double>(now.QuadPart - previous);
    if (elapsed <= 0)
    {
        return;
    }

    const double fps = static_cast<double>(g_frequency.QuadPart) * 1000000000.0 / elapsed;
    if (std::isfinite(fps) && fps >= 0.0 && fps <= 1000.0)
    {
        g_currentFps.store(static_cast<float>(fps), std::memory_order_relaxed);
    }
}

HRESULT WINAPI HookPresent(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags)
{
    RecordFps();
    return g_originalPresent(swapChain, syncInterval, flags);
}

HRESULT WINAPI HookPresent1(
    IDXGISwapChain1* swapChain,
    UINT syncInterval,
    UINT flags,
    const DXGI_PRESENT_PARAMETERS* parameters)
{
    RecordFps();
    return g_originalPresent1(swapChain, syncInterval, flags, parameters);
}

bool InstallHooksFromSwapChain(IDXGISwapChain* swapChain)
{
    if (swapChain == nullptr)
    {
        return false;
    }

    void** vtable = *reinterpret_cast<void***>(swapChain);
    auto present = reinterpret_cast<PresentFunction>(vtable[8]);
    const MH_STATUS createResult = MH_CreateHook(
        reinterpret_cast<LPVOID>(present),
        reinterpret_cast<LPVOID>(&HookPresent),
        reinterpret_cast<LPVOID*>(&g_originalPresent));
    if (createResult != MH_OK)
    {
        return false;
    }

    MH_QueueEnableHook(reinterpret_cast<LPVOID>(present));

    IDXGISwapChain1* swapChain1 = nullptr;
    if (SUCCEEDED(swapChain->QueryInterface(__uuidof(IDXGISwapChain1), reinterpret_cast<void**>(&swapChain1))) && swapChain1 != nullptr)
    {
        void** vtable1 = *reinterpret_cast<void***>(swapChain1);
        auto present1 = reinterpret_cast<Present1Function>(vtable1[22]);
        if (reinterpret_cast<LPVOID>(present1) != reinterpret_cast<LPVOID>(present))
        {
            const MH_STATUS createPresent1Result = MH_CreateHook(
                reinterpret_cast<LPVOID>(present1),
                reinterpret_cast<LPVOID>(&HookPresent1),
                reinterpret_cast<LPVOID*>(&g_originalPresent1));
            if (createPresent1Result == MH_OK)
            {
                MH_QueueEnableHook(reinterpret_cast<LPVOID>(present1));
            }
        }
        swapChain1->Release();
    }

    return MH_ApplyQueued() == MH_OK;
}

bool TryCreateD3D11SwapChain(IDXGISwapChain** output)
{
    IDXGISwapChain* swapChain = nullptr;
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    DXGI_SWAP_CHAIN_DESC description{};
    description.BufferCount = 1;
    description.BufferDesc.Width = 1;
    description.BufferDesc.Height = 1;
    description.BufferDesc.RefreshRate.Numerator = 60;
    description.BufferDesc.RefreshRate.Denominator = 1;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.OutputWindow = GetDesktopWindow();
    description.SampleDesc.Count = 1;
    description.Windowed = TRUE;
    description.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    description.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;

    HRESULT result = D3D11CreateDeviceAndSwapChain(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &description,
        &swapChain,
        &device,
        nullptr,
        &context);
    if (FAILED(result))
    {
        result = D3D11CreateDeviceAndSwapChain(
            nullptr,
            D3D_DRIVER_TYPE_WARP,
            nullptr,
            0,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            &description,
            &swapChain,
            &device,
            nullptr,
            &context);
    }

    if (SUCCEEDED(result) && swapChain != nullptr)
    {
        *output = swapChain;
        swapChain = nullptr;
    }

    if (context != nullptr) context->Release();
    if (device != nullptr) device->Release();
    if (swapChain != nullptr) swapChain->Release();
    return SUCCEEDED(result) && *output != nullptr;
}

bool TryCreateD3D12SwapChain(IDXGISwapChain** output)
{
    ID3D12Device* device = nullptr;
    ID3D12CommandQueue* queue = nullptr;
    IDXGIFactory2* factory = nullptr;
    IDXGISwapChain1* swapChain1 = nullptr;

    HRESULT result = D3D12CreateDevice(
        nullptr,
        D3D_FEATURE_LEVEL_11_0,
        IID_PPV_ARGS(&device));
    if (SUCCEEDED(result))
    {
        D3D12_COMMAND_QUEUE_DESC queueDescription{};
        queueDescription.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        result = device->CreateCommandQueue(&queueDescription, IID_PPV_ARGS(&queue));
    }
    if (SUCCEEDED(result))
    {
        result = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
    }
    if (SUCCEEDED(result))
    {
        DXGI_SWAP_CHAIN_DESC1 description{};
        description.Width = 1;
        description.Height = 1;
        description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        description.SampleDesc.Count = 1;
        description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        description.BufferCount = 2;
        description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        result = factory->CreateSwapChainForHwnd(
            queue,
            GetDesktopWindow(),
            &description,
            nullptr,
            nullptr,
            &swapChain1);
    }

    if (SUCCEEDED(result) && swapChain1 != nullptr)
    {
        *output = static_cast<IDXGISwapChain*>(swapChain1);
        swapChain1 = nullptr;
    }

    if (swapChain1 != nullptr) swapChain1->Release();
    if (factory != nullptr) factory->Release();
    if (queue != nullptr) queue->Release();
    if (device != nullptr) device->Release();
    return SUCCEEDED(result) && *output != nullptr;
}

bool CreatePresentHook()
{
    const MH_STATUS initializeResult = MH_Initialize();
    if (initializeResult != MH_OK && initializeResult != MH_ERROR_ALREADY_INITIALIZED)
    {
        return false;
    }

    IDXGISwapChain* swapChain = nullptr;
    const bool created = TryCreateD3D11SwapChain(&swapChain) || TryCreateD3D12SwapChain(&swapChain);
    if (!created)
    {
        return false;
    }

    const bool installed = InstallHooksFromSwapChain(swapChain);
    swapChain->Release();
    return installed;
}

DWORD WINAPI InitializeThread(LPVOID parameter)
{
    (void)parameter;
    g_configMapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, ConfigMappingName);
    if (g_configMapping != nullptr)
    {
        g_configView = MapViewOfFile(g_configMapping, FILE_MAP_ALL_ACCESS, 0, 0, 0);
    }

    QueryPerformanceFrequency(&g_frequency);
    bool hookReady = false;
    unsigned int retryCount = 0;
    while (true)
    {
        RefreshConfig();
        if (!hookReady && retryCount++ % 20 == 0)
        {
            hookReady = CreatePresentHook();
            WriteHookReady(hookReady);
        }

        Sleep(50);
    }

    return 0;
}
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        CreateThread(nullptr, 0, &InitializeThread, module, 0, nullptr);
    }

    return TRUE;
}
