#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d9.h>
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
constexpr uint32_t ConfigVersion = 3;
// The trailing 3 is the layout version: a helper left loaded in a game by an older build keeps its own block
// and cannot interfere with the one the current app uses.
constexpr wchar_t ConfigMappingName[] = L"Local\\ThermalScopeFpsConfig3";
constexpr double StaleSwapChainSeconds = 1.0;
constexpr double PublishWindowSeconds = 0.5;
constexpr uint32_t FrameRingSize = 240;
constexpr uint32_t MinTargetFps = 10;
constexpr uint32_t MaxTargetFps = 1000;

// Layout must match GameFpsMonitor.cs (offsets in bytes).
struct SharedConfig
{
    uint32_t magic;          // 0
    uint32_t version;        // 4
    uint32_t controllerPid;  // 8
    uint32_t targetPid;      // 12
    uint32_t reserved;       // 16
    uint32_t enabled;        // 20
    uint32_t hookReady;      // 24
    uint32_t avgFpsBits;     // 28  average FPS over the last publish window
    uint32_t lowFpsBits;     // 32  FPS of the slowest frame in the window
    uint32_t frameTimeBits;  // 36  average frame time in milliseconds
    uint32_t heartbeat;      // 40  incremented on every published window
    uint32_t targetFps;      // 44  frame limiter target, 0 = unlimited (controller -> helper)
    uint32_t frameCount;     // 48  total frames written to frameTimesMs
    uint32_t pacingMode;     // 52  0 = precise timer, 1 = lock to the display refresh (controller -> helper)
    uint32_t refreshHz;      // 56  refresh rate of the game's display (helper -> controller)
    uint32_t reserved2;      // 60
    float frameTimesMs[FrameRingSize]; // 64  ring buffer of the most recent frame intervals
};

using PresentFunction = HRESULT(WINAPI*)(IDXGISwapChain*, UINT, UINT);
using Present1Function = HRESULT(WINAPI*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);

PresentFunction g_originalPresent = nullptr;
Present1Function g_originalPresent1 = nullptr;
std::atomic<bool> g_enabled{ false };
std::atomic<void*> g_activeSwapChain{ nullptr };
std::atomic<int64_t> g_lastPresent{ 0 };
LARGE_INTEGER g_frequency{ };
HANDLE g_configMapping = nullptr;
SharedConfig* g_config = nullptr;

HANDLE g_timer = nullptr;      // high-resolution waitable timer for the coarse part of a wait
std::atomic<int64_t> g_nextFrame{ 0 }; // absolute QPC deadline of the next Present
SRWLOCK g_statsLock = SRWLOCK_INIT;    // guards the window accumulators and the published stats block

// Accumulators touched only by the thread presenting the active swap chain.
double g_windowSeconds = 0.0;
double g_windowSlowest = 0.0;
uint32_t g_windowFrames = 0;

uint32_t FloatBits(float value)
{
    uint32_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
}

void PublishFps(float average, float low, float frameTimeMs)
{
    if (g_config == nullptr)
    {
        return;
    }

    // Callers hold g_statsLock (StatsGuard): the render thread and the config thread both write here.
    g_config->avgFpsBits = FloatBits(average);
    g_config->lowFpsBits = FloatBits(low);
    g_config->frameTimeBits = FloatBits(frameTimeMs);
    g_config->heartbeat++;
}

struct StatsGuard
{
    StatsGuard() { AcquireSRWLockExclusive(&g_statsLock); }
    ~StatsGuard() { ReleaseSRWLockExclusive(&g_statsLock); }
    StatsGuard(const StatsGuard&) = delete;
    StatsGuard& operator=(const StatsGuard&) = delete;
};

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

int64_t NowTicks()
{
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    return now.QuadPart;
}

double SecondsSince(int64_t earlier, int64_t now)
{
    return static_cast<double>(now - earlier) / static_cast<double>(g_frequency.QuadPart);
}

void ResetWindow()
{
    g_windowSeconds = 0.0;
    g_windowSlowest = 0.0;
    g_windowFrames = 0;
}

// Waits until the QPC deadline: a high-resolution timer for the bulk, then a short spin so the
// frame lands within microseconds of the target instead of a scheduler tick (~1-15 ms) late.
void SleepUntil(int64_t deadline)
{
    for (;;)
    {
        const int64_t remaining = deadline - NowTicks();
        if (remaining <= 0)
        {
            return;
        }

        const double ms = static_cast<double>(remaining) * 1000.0 / static_cast<double>(g_frequency.QuadPart);
        if (ms > 2.0)
        {
            if (g_timer != nullptr)
            {
                LARGE_INTEGER due{};
                due.QuadPart = -static_cast<LONGLONG>((ms - 1.5) * 10000.0); // relative, 100 ns units
                if (SetWaitableTimer(g_timer, &due, 0, nullptr, nullptr, FALSE))
                {
                    WaitForSingleObject(g_timer, 100);
                    continue;
                }
            }

            Sleep(1);
        }
        else
        {
            YieldProcessor();
        }
    }
}

// Evenly spaced presents: every frame is due one interval after the previous deadline, so a slow
// frame is followed by a short wait (cadence is kept) and the error never accumulates.
void LimitFrame(void* swapChain)
{
    if (g_config == nullptr || !g_enabled.load(std::memory_order_acquire) || g_frequency.QuadPart <= 0)
    {
        g_nextFrame.store(0, std::memory_order_relaxed);
        return;
    }

    const uint32_t fps = g_config->targetFps;
    void* active = g_activeSwapChain.load(std::memory_order_relaxed);
    if (fps < MinTargetFps || fps > MaxTargetFps || (active != nullptr && active != swapChain))
    {
        g_nextFrame.store(0, std::memory_order_relaxed);
        return;
    }

    const int64_t interval = g_frequency.QuadPart / static_cast<int64_t>(fps);
    const int64_t now = NowTicks();
    int64_t deadline = g_nextFrame.load(std::memory_order_relaxed);
    if (deadline == 0 || now > deadline + interval)
    {
        deadline = now; // first frame, or we fell more than a frame behind: resynchronise
    }

    SleepUntil(deadline);
    g_nextFrame.store(deadline + interval, std::memory_order_relaxed);
}

std::atomic<uint32_t> g_refreshHz{ 0 };
std::atomic<int64_t> g_refreshStamp{ 0 };

// True about once a second: the refresh rate and window of a game can change or move, so re-probe then.
bool RefreshDue(int64_t now)
{
    const int64_t stamp = g_refreshStamp.load(std::memory_order_relaxed);
    if (stamp != 0 && SecondsSince(stamp, now) < 1.0)
    {
        return false;
    }

    g_refreshStamp.store(now, std::memory_order_relaxed);
    return true;
}

// Refresh rate of the monitor the game window is on. Returns that monitor (null when unknown).
HMONITOR UpdateRefreshRate(HWND window)
{
    if (window == nullptr)
    {
        return nullptr;
    }

    HMONITOR handle = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
    MONITORINFOEXW monitor{};
    monitor.cbSize = sizeof(monitor);
    if (!GetMonitorInfoW(handle, &monitor))
    {
        return nullptr;
    }

    DEVMODEW mode{};
    mode.dmSize = sizeof(mode);
    if (EnumDisplaySettingsW(monitor.szDevice, ENUM_CURRENT_SETTINGS, &mode) && mode.dmDisplayFrequency > 1)
    {
        g_refreshHz.store(mode.dmDisplayFrequency, std::memory_order_relaxed);
        if (g_config != nullptr)
        {
            g_config->refreshHz = mode.dmDisplayFrequency;
        }
    }

    return handle;
}

// How many refreshes each frame should stay up so the target is a whole fraction of the display rate
// (60 on 60 Hz, 30 on 60 Hz, 72 on 144 Hz ...); 0 when the mode does not apply.
uint32_t LockDivisor(void* swapChain)
{
    if (g_config == nullptr || !g_enabled.load(std::memory_order_acquire) || g_config->pacingMode != 1)
    {
        return 0;
    }

    const uint32_t fps = g_config->targetFps;
    const uint32_t hz = g_refreshHz.load(std::memory_order_relaxed);
    void* active = g_activeSwapChain.load(std::memory_order_relaxed);
    if (fps < MinTargetFps || hz < 24 || hz > 1000 || (active != nullptr && active != swapChain))
    {
        return 0;
    }

    const uint32_t divisor = (hz + fps / 2) / fps;
    if (divisor < 1 || divisor > 4 || std::fabs(static_cast<double>(hz) / divisor - fps) > 1.0)
    {
        return 0;
    }

    return divisor;
}

// Console-style pacing: let the display's vertical blank set the pace instead of a timer. Every frame then
// stays on screen for exactly the same number of refreshes, which a timer cannot guarantee (timer and
// display clocks always drift apart).
bool LockToRefresh(void* swapChain, UINT& syncInterval, UINT& flags)
{
    const uint32_t divisor = LockDivisor(swapChain);
    if (divisor == 0)
    {
        return false;
    }

    syncInterval = divisor;
    flags &= ~static_cast<UINT>(DXGI_PRESENT_ALLOW_TEARING);
    return true;
}

void RefreshConfig(bool checkController)
{
    if (g_config == nullptr)
    {
        g_enabled.store(false, std::memory_order_release);
        return;
    }

    // The block belongs to whichever game the controller attached last. A helper still loaded in an
    // earlier game must stay out of the way: no limiting and no writes into the new game's stats.
    const bool mine = g_config->magic == ConfigMagic &&
        g_config->version == ConfigVersion &&
        g_config->targetPid == GetCurrentProcessId();
    bool valid = mine && g_config->enabled != 0;
    if (valid && checkController)
    {
        valid = IsControllerAlive(g_config->controllerPid);
    }

    g_enabled.store(valid, std::memory_order_release);
    if (!mine)
    {
        return;
    }

    // The controller zeroes this block when it (re)attaches, so keep re-asserting readiness.
    // Ready means a frame was actually seen: a hook on an API the game does not use must not report success.
    g_config->hookReady = g_activeSwapChain.load(std::memory_order_relaxed) != nullptr ? 1u : 0u;

    // No frames for a while (paused, minimised, loading screen): report 0 instead of a stale value.
    const int64_t last = g_lastPresent.load(std::memory_order_relaxed);
    if (!valid || last == 0 || g_frequency.QuadPart <= 0 ||
        SecondsSince(last, NowTicks()) > StaleSwapChainSeconds)
    {
        StatsGuard guard;
        PublishFps(0.0f, 0.0f, 0.0f);
    }
}

void RecordFrame(void* swapChain)
{
    if (!g_enabled.load(std::memory_order_acquire) || g_frequency.QuadPart <= 0)
    {
        return;
    }

    StatsGuard guard; // window accumulators, ring buffer and published stats are shared with the config thread
    const int64_t now = NowTicks();
    const int64_t last = g_lastPresent.load(std::memory_order_relaxed);
    void* active = g_activeSwapChain.load(std::memory_order_relaxed);

    // Overlays and secondary windows create swap chains of their own; follow a single one so
    // their presents are not counted as game frames. Adopt another chain only when the old one went quiet.
    if (active != swapChain)
    {
        const bool activeIsStale = active == nullptr || last == 0 || SecondsSince(last, now) > StaleSwapChainSeconds;
        if (!activeIsStale)
        {
            return;
        }

        g_activeSwapChain.store(swapChain, std::memory_order_relaxed);
        g_lastPresent.store(now, std::memory_order_relaxed);
        ResetWindow();
        return;
    }

    g_lastPresent.store(now, std::memory_order_relaxed);
    if (last == 0)
    {
        return;
    }

    const double delta = SecondsSince(last, now);
    if (delta <= 0.0 || delta > StaleSwapChainSeconds)
    {
        ResetWindow();
        return;
    }

    if (g_config != nullptr)
    {
        const uint32_t index = g_config->frameCount;
        g_config->frameTimesMs[index % FrameRingSize] = static_cast<float>(delta * 1000.0);
        g_config->frameCount = index + 1;
    }

    g_windowSeconds += delta;
    g_windowFrames++;
    if (delta > g_windowSlowest)
    {
        g_windowSlowest = delta;
    }

    if (g_windowSeconds >= PublishWindowSeconds)
    {
        const double average = static_cast<double>(g_windowFrames) / g_windowSeconds;
        const double low = g_windowSlowest > 0.0 ? 1.0 / g_windowSlowest : 0.0;
        const double frameTimeMs = g_windowSeconds * 1000.0 / static_cast<double>(g_windowFrames);
        if (std::isfinite(average) && average <= 20000.0)
        {
            PublishFps(static_cast<float>(average), static_cast<float>(low), static_cast<float>(frameTimeMs));
        }

        ResetWindow();
    }
}

// Shared by both Present flavours; DXGI_PRESENT_TEST calls are not frames and are left alone.
void PaceFrame(IDXGISwapChain* swapChain, UINT& syncInterval, UINT& flags)
{
    if (flags & DXGI_PRESENT_TEST)
    {
        return;
    }

    if (RefreshDue(NowTicks()))
    {
        DXGI_SWAP_CHAIN_DESC description{};
        if (SUCCEEDED(swapChain->GetDesc(&description)))
        {
            UpdateRefreshRate(description.OutputWindow);
        }
    }

    if (LockToRefresh(swapChain, syncInterval, flags))
    {
        g_nextFrame.store(0, std::memory_order_relaxed); // the vblank paces; leave no stale timer deadline behind
    }
    else
    {
        LimitFrame(swapChain);
    }

    RecordFrame(swapChain);
}

HRESULT WINAPI HookPresent(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags)
{
    PaceFrame(swapChain, syncInterval, flags);
    return g_originalPresent(swapChain, syncInterval, flags);
}

HRESULT WINAPI HookPresent1(
    IDXGISwapChain1* swapChain,
    UINT syncInterval,
    UINT flags,
    const DXGI_PRESENT_PARAMETERS* parameters)
{
    PaceFrame(swapChain, syncInterval, flags);
    return g_originalPresent1(swapChain, syncInterval, flags, parameters);
}

// ---- Direct3D 9 (most 32-bit games) -------------------------------------------------------------
using D3D9PresentFunction = HRESULT(WINAPI*)(void*, const RECT*, const RECT*, HWND, const RGNDATA*);
using D3D9PresentExFunction = HRESULT(WINAPI*)(void*, const RECT*, const RECT*, HWND, const RGNDATA*, DWORD);
using D3D9SwapPresentFunction = HRESULT(WINAPI*)(void*, const RECT*, const RECT*, HWND, const RGNDATA*, DWORD);

D3D9PresentFunction g_d3d9DevicePresent = nullptr;     // IDirect3DDevice9::Present
D3D9PresentFunction g_d3d9ExDevicePresent = nullptr;   // same slot on a D3D9Ex device, when it is a different function
D3D9PresentExFunction g_d3d9PresentEx = nullptr;       // IDirect3DDevice9Ex::PresentEx
D3D9SwapPresentFunction g_d3d9SwapPresent = nullptr;   // IDirect3DSwapChain9::Present

// Locked mode for Direct3D 9. D3D9 fixes its vsync interval when the device is created, so unlike DXGI it
// cannot be changed per Present. Instead wait for whole vertical blanks here: after the previous present
// (which left at a vblank) hold the next one back until `divisor - own` more blanks have passed, where `own`
// is the interval the game's own Present already waits for (0 = immediate, no vsync).
std::atomic<int64_t> g_d3d9Returned{ 0 };   // QPC when the last Present of the active swap chain returned
std::atomic<uint32_t> g_d3d9Interval{ 0 };  // refreshes the game's own Present waits: 0 = immediate, 1..4
IDXGIOutput* g_vblankOutput = nullptr;      // output of the game's monitor, used only for WaitForVBlank
HMONITOR g_vblankMonitor = nullptr;

void SelectVBlankOutput(HMONITOR monitor)
{
    if (monitor == nullptr || (monitor == g_vblankMonitor && g_vblankOutput != nullptr))
    {
        return;
    }

    IDXGIOutput* found = nullptr;
    IDXGIFactory1* factory = nullptr;
    if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))))
    {
        IDXGIAdapter1* adapter = nullptr;
        for (UINT a = 0; found == nullptr && factory->EnumAdapters1(a, &adapter) != DXGI_ERROR_NOT_FOUND; ++a)
        {
            IDXGIOutput* output = nullptr;
            for (UINT o = 0; found == nullptr && adapter->EnumOutputs(o, &output) != DXGI_ERROR_NOT_FOUND; ++o)
            {
                DXGI_OUTPUT_DESC description{};
                if (SUCCEEDED(output->GetDesc(&description)) && description.Monitor == monitor)
                {
                    found = output;
                }
                else
                {
                    output->Release();
                }
            }
            adapter->Release();
        }
        factory->Release();
    }

    // Only the presenting thread gets here, so no other thread is inside WaitForVBlank on the old output.
    if (g_vblankOutput != nullptr)
    {
        g_vblankOutput->Release();
    }
    g_vblankOutput = found;
    g_vblankMonitor = monitor;
}

// Once a second: refresh rate, monitor and the game's own vsync interval.
void ProbeD3D9(void* object, bool isDevice, HWND hint)
{
    IDirect3DSwapChain9* chain = nullptr;
    bool release = false;
    HWND window = nullptr;
    if (isDevice)
    {
        auto* device = static_cast<IDirect3DDevice9*>(object);
        release = SUCCEEDED(device->GetSwapChain(0, &chain)) && chain != nullptr;
        D3DDEVICE_CREATION_PARAMETERS creation{};
        if (SUCCEEDED(device->GetCreationParameters(&creation)))
        {
            window = creation.hFocusWindow;
        }
    }
    else
    {
        chain = static_cast<IDirect3DSwapChain9*>(object);
    }

    D3DPRESENT_PARAMETERS parameters{};
    if (chain != nullptr && SUCCEEDED(chain->GetPresentParameters(&parameters)))
    {
        // A windowed Present hands the frame to the desktop compositor and returns at once, vsync or not;
        // only exclusive fullscreen actually blocks for the interval.
        switch (parameters.Windowed ? D3DPRESENT_INTERVAL_IMMEDIATE : parameters.PresentationInterval)
        {
        case D3DPRESENT_INTERVAL_IMMEDIATE: g_d3d9Interval.store(0, std::memory_order_relaxed); break;
        case D3DPRESENT_INTERVAL_TWO: g_d3d9Interval.store(2, std::memory_order_relaxed); break;
        case D3DPRESENT_INTERVAL_THREE: g_d3d9Interval.store(3, std::memory_order_relaxed); break;
        case D3DPRESENT_INTERVAL_FOUR: g_d3d9Interval.store(4, std::memory_order_relaxed); break;
        default: g_d3d9Interval.store(1, std::memory_order_relaxed); break; // DEFAULT and ONE
        }

        if (parameters.hDeviceWindow != nullptr)
        {
            window = parameters.hDeviceWindow;
        }
    }

    if (release)
    {
        chain->Release();
    }

    if (window == nullptr)
    {
        window = hint != nullptr ? hint : GetForegroundWindow();
    }

    SelectVBlankOutput(UpdateRefreshRate(window));
}

bool LockD3D9(void* object)
{
    const uint32_t divisor = LockDivisor(object);
    const uint32_t own = g_d3d9Interval.load(std::memory_order_relaxed);
    if (divisor == 0 || own > divisor || g_vblankOutput == nullptr)
    {
        return false;
    }

    const int64_t returned = g_d3d9Returned.load(std::memory_order_relaxed);
    if (returned != 0) // the first frame has no phase to hold on to
    {
        // Blanks that went by while the game rendered; the 0.05 absorbs the few microseconds between the
        // blank and the moment the last Present returned.
        const double hz = static_cast<double>(g_refreshHz.load(std::memory_order_relaxed));
        const uint32_t passed = static_cast<uint32_t>(SecondsSince(returned, NowTicks()) * hz + 0.05);
        for (uint32_t i = passed; i < divisor - own; ++i)
        {
            if (FAILED(g_vblankOutput->WaitForVBlank()))
            {
                return false; // fall back to the timer limiter
            }
        }
    }

    return true;
}

void PaceD3D9(void* object, bool isDevice, HWND hint)
{
    if (RefreshDue(NowTicks()))
    {
        ProbeD3D9(object, isDevice, hint);
    }

    if (LockD3D9(object))
    {
        g_nextFrame.store(0, std::memory_order_relaxed); // the vblank paces; leave no stale timer deadline behind
    }
    else
    {
        LimitFrame(object);
    }

    RecordFrame(object);
}

void PresentedD3D9(void* object)
{
    if (g_activeSwapChain.load(std::memory_order_relaxed) == object)
    {
        g_d3d9Returned.store(NowTicks(), std::memory_order_relaxed);
    }
}

// A device and its swap chain can both be hooked (and one may call the other): only the first
// caller through becomes the active "swap chain", the nested call is ignored by both helpers.
HRESULT WINAPI HookD3D9DevicePresent(void* device, const RECT* a, const RECT* b, HWND c, const RGNDATA* d)
{
    PaceD3D9(device, true, c);
    const HRESULT result = g_d3d9DevicePresent(device, a, b, c, d);
    PresentedD3D9(device);
    return result;
}

HRESULT WINAPI HookD3D9ExDevicePresent(void* device, const RECT* a, const RECT* b, HWND c, const RGNDATA* d)
{
    PaceD3D9(device, true, c);
    const HRESULT result = g_d3d9ExDevicePresent(device, a, b, c, d);
    PresentedD3D9(device);
    return result;
}

HRESULT WINAPI HookD3D9PresentEx(void* device, const RECT* a, const RECT* b, HWND c, const RGNDATA* d, DWORD flags)
{
    PaceD3D9(device, true, c);
    const HRESULT result = g_d3d9PresentEx(device, a, b, c, d, flags);
    PresentedD3D9(device);
    return result;
}

HRESULT WINAPI HookD3D9SwapPresent(void* swapChain, const RECT* a, const RECT* b, HWND c, const RGNDATA* d, DWORD flags)
{
    PaceD3D9(swapChain, false, c);
    const HRESULT result = g_d3d9SwapPresent(swapChain, a, b, c, d, flags);
    PresentedD3D9(swapChain);
    return result;
}

// Diagnostics for support: set THERMALSCOPE_HOOK_LOG to a file path before starting the game.
void Log(const char* text, long long a = 0, long long b = 0)
{
    wchar_t path[MAX_PATH];
    if (GetEnvironmentVariableW(L"THERMALSCOPE_HOOK_LOG", path, MAX_PATH) == 0)
    {
        return;
    }

    char line[256];
    const int length = wsprintfA(line, "%s %d %d\r\n", text, static_cast<int>(a), static_cast<int>(b));
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE)
    {
        DWORD written = 0;
        WriteFile(file, line, static_cast<DWORD>(length), &written, nullptr);
        CloseHandle(file);
    }
}

bool HookTarget(void* target, void* detour, void** original)
{
    if (target == nullptr)
    {
        return false;
    }

    const MH_STATUS created = MH_CreateHook(target, detour, original);
    Log("d3d9 hook create", created, reinterpret_cast<long long>(target));
    return created == MH_OK && MH_QueueEnableHook(target) == MH_OK;
}

bool InstallD3D9Hooks(HWND window)
{
    D3DPRESENT_PARAMETERS parameters{};
    parameters.Windowed = TRUE;
    parameters.SwapEffect = D3DSWAPEFFECT_DISCARD;
    parameters.BackBufferFormat = D3DFMT_UNKNOWN;
    parameters.BackBufferWidth = 8;
    parameters.BackBufferHeight = 8;
    parameters.BackBufferCount = 1;
    parameters.hDeviceWindow = window;
    const DWORD behaviour = D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_FPU_PRESERVE;

    void* devicePresent = nullptr;
    void* swapPresent = nullptr;
    IDirect3D9* d3d = Direct3DCreate9(D3D_SDK_VERSION);
    if (d3d != nullptr)
    {
        IDirect3DDevice9* device = nullptr;
        const HRESULT created = d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, window, behaviour, &parameters, &device);
        Log("d3d9 CreateDevice", created);
        if (SUCCEEDED(created) && device != nullptr)
        {
            devicePresent = (*reinterpret_cast<void***>(device))[17];
            IDirect3DSwapChain9* swap = nullptr;
            if (SUCCEEDED(device->GetSwapChain(0, &swap)) && swap != nullptr)
            {
                swapPresent = (*reinterpret_cast<void***>(swap))[3];
                swap->Release();
            }
            device->Release();
        }
        d3d->Release();
    }

    // Games that use Direct3DCreate9Ex get a different runtime object; hook what it uses as well.
    void* exDevicePresent = nullptr;
    void* presentEx = nullptr;
    using CreateEx = HRESULT(WINAPI*)(UINT, IDirect3D9Ex**);
    const HMODULE d3d9Module = GetModuleHandleW(L"d3d9.dll");
    const auto createEx = d3d9Module != nullptr ? reinterpret_cast<CreateEx>(GetProcAddress(d3d9Module, "Direct3DCreate9Ex")) : nullptr;
    IDirect3D9Ex* d3dEx = nullptr;
    if (createEx != nullptr && SUCCEEDED(createEx(D3D_SDK_VERSION, &d3dEx)) && d3dEx != nullptr)
    {
        IDirect3DDevice9Ex* device = nullptr;
        if (SUCCEEDED(d3dEx->CreateDeviceEx(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, window, behaviour, &parameters, nullptr, &device)) && device != nullptr)
        {
            void** vtable = *reinterpret_cast<void***>(device);
            exDevicePresent = vtable[17];
            presentEx = vtable[121];
            if (swapPresent == nullptr)
            {
                IDirect3DSwapChain9* swap = nullptr;
                if (SUCCEEDED(device->GetSwapChain(0, &swap)) && swap != nullptr)
                {
                    swapPresent = (*reinterpret_cast<void***>(swap))[3];
                    swap->Release();
                }
            }
            device->Release();
        }
        d3dEx->Release();
    }

    bool any = false;
    any |= HookTarget(devicePresent, reinterpret_cast<void*>(&HookD3D9DevicePresent), reinterpret_cast<void**>(&g_d3d9DevicePresent));
    if (exDevicePresent != devicePresent)
    {
        any |= HookTarget(exDevicePresent, reinterpret_cast<void*>(&HookD3D9ExDevicePresent), reinterpret_cast<void**>(&g_d3d9ExDevicePresent));
    }
    any |= HookTarget(presentEx, reinterpret_cast<void*>(&HookD3D9PresentEx), reinterpret_cast<void**>(&g_d3d9PresentEx));
    any |= HookTarget(swapPresent, reinterpret_cast<void*>(&HookD3D9SwapPresent), reinterpret_cast<void**>(&g_d3d9SwapPresent));
    const MH_STATUS applied = any ? MH_ApplyQueued() : MH_ERROR_NOT_CREATED;
    Log("d3d9 apply", applied, any);
    return applied == MH_OK;
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

bool TryCreateD3D11SwapChain(HWND window, IDXGISwapChain** output)
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
    description.OutputWindow = window;
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

bool TryCreateD3D12SwapChain(HWND window, IDXGISwapChain** output)
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
            window,
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

// A hidden 8x8 window owned by this process: DXGI refuses swap chains on windows of other processes
// (such as the desktop window), so the throwaway swap chain needs a window of its own.
HWND CreateDummyWindow()
{
    static const wchar_t className[] = L"ThermalScopeDummyWindow";
    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = className;
    RegisterClassExW(&windowClass); // fails harmlessly when the class already exists
    return CreateWindowExW(0, className, L"", WS_OVERLAPPED, 0, 0, 8, 8, nullptr, nullptr, windowClass.hInstance, nullptr);
}

bool g_dxgiHooked = false;
bool g_d3d9Hooked = false;

// Hooks whatever the game renders with. Direct3D 9 is only probed once the game has loaded d3d9.dll,
// so a DXGI game is never pushed into loading it.
void InstallPresentHooks()
{
    const MH_STATUS initializeResult = MH_Initialize();
    if (initializeResult != MH_OK && initializeResult != MH_ERROR_ALREADY_INITIALIZED)
    {
        return;
    }

    HWND window = CreateDummyWindow();
    if (window == nullptr)
    {
        return;
    }

    if (!g_dxgiHooked)
    {
        IDXGISwapChain* swapChain = nullptr;
        if (TryCreateD3D11SwapChain(window, &swapChain) || TryCreateD3D12SwapChain(window, &swapChain))
        {
            g_dxgiHooked = InstallHooksFromSwapChain(swapChain);
            swapChain->Release();
        }
    }

    if (!g_d3d9Hooked && GetModuleHandleW(L"d3d9.dll") != nullptr)
    {
        g_d3d9Hooked = InstallD3D9Hooks(window);
    }

    DestroyWindow(window);
}

DWORD WINAPI InitializeThread(LPVOID parameter)
{
    (void)parameter;
    g_configMapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, ConfigMappingName);
    if (g_configMapping != nullptr)
    {
        g_config = static_cast<SharedConfig*>(
            MapViewOfFile(g_configMapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(SharedConfig)));
    }

    QueryPerformanceFrequency(&g_frequency);
    // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION (Windows 10 1803+); null falls back to Sleep(1).
    g_timer = CreateWaitableTimerExW(nullptr, nullptr, 0x00000002, TIMER_ALL_ACCESS);
    unsigned int tick = 0;
    while (true)
    {
        // Poll the (cheap) config block often, but only probe the controller process every ~250 ms.
        RefreshConfig(tick % 5 == 0);
        if (tick % 20 == 0 && (!g_dxgiHooked || (!g_d3d9Hooked && GetModuleHandleW(L"d3d9.dll") != nullptr)))
        {
            InstallPresentHooks();
        }

        tick++;
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
