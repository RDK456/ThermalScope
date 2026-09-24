using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ThermalScope.Services;

internal static class NativeProcessInjector
{
    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x0004;
    private const uint Th32csSnapModule = 0x00000008;
    private const uint Th32csSnapModule32 = 0x00000010;
    private const ushort ImageFileMachineUnknown = 0x0000;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineAmd64 = 0x8664;

    public static bool IsSupportedArchitecture(int processId, out string error)
    {
        error = string.Empty;
        var process = OpenProcess(ProcessQueryInformation, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            error = Win32Error("OpenProcess");
            return false;
        }

        try
        {
            if (IsWow64Process2(process, out var processMachine, out var nativeMachine))
            {
                if (processMachine == ImageFileMachineI386)
                {
                    error = "The selected game is 32-bit. The standalone FPS monitor currently supports 64-bit DirectX games.";
                    return false;
                }

                if (processMachine is not (ImageFileMachineUnknown or ImageFileMachineAmd64))
                {
                    error = "The selected game uses an unsupported process architecture.";
                    return false;
                }

                if (nativeMachine is not (ImageFileMachineUnknown or ImageFileMachineAmd64))
                {
                    error = "The selected game is not a native x64 process.";
                    return false;
                }

                return true;
            }

            if (IsWow64Process(process, out var isWow64) && isWow64)
            {
                error = "The selected game is 32-bit. The standalone FPS monitor currently supports 64-bit DirectX games.";
                return false;
            }

            return true;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public static bool Inject(int processId, string dllPath, out string error)
    {
        error = string.Empty;
        if (!File.Exists(dllPath))
        {
            error = "The built-in FPS monitor DLL is missing.";
            return false;
        }

        if (!IsSupportedArchitecture(processId, out error))
        {
            return false;
        }

        var access = ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite;
        var process = OpenProcess(access, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            error = Win32Error("OpenProcess");
            return false;
        }

        var remotePath = IntPtr.Zero;
        var remoteThread = IntPtr.Zero;
        try
        {
            var pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            remotePath = VirtualAllocEx(
                process,
                IntPtr.Zero,
                (UIntPtr)pathBytes.Length,
                MemCommit | MemReserve,
                PageReadWrite);
            if (remotePath == IntPtr.Zero)
            {
                error = Win32Error("VirtualAllocEx");
                return false;
            }

            if (!WriteProcessMemory(process, remotePath, pathBytes, (UIntPtr)pathBytes.Length, out _))
            {
                error = Win32Error("WriteProcessMemory");
                return false;
            }

            var localKernel32 = GetModuleHandle("kernel32.dll");
            var localLoadLibrary = GetProcAddress(localKernel32, "LoadLibraryW");
            var remoteKernel32 = FindRemoteModuleBase(processId, "kernel32.dll");
            if (localKernel32 == IntPtr.Zero || localLoadLibrary == IntPtr.Zero || remoteKernel32 == IntPtr.Zero)
            {
                error = "Could not locate LoadLibraryW in the target process.";
                return false;
            }

            var loadLibraryOffset = localLoadLibrary.ToInt64() - localKernel32.ToInt64();
            var remoteLoadLibrary = new IntPtr(remoteKernel32.ToInt64() + loadLibraryOffset);
            remoteThread = CreateRemoteThread(process, IntPtr.Zero, 0, remoteLoadLibrary, remotePath, 0, IntPtr.Zero);
            if (remoteThread == IntPtr.Zero)
            {
                error = Win32Error("CreateRemoteThread");
                return false;
            }

            var waitResult = WaitForSingleObject(remoteThread, 10_000);
            if (waitResult != 0)
            {
                error = "The target process did not finish loading the FPS monitor in time.";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            error = $"Injection failed: {exception.Message}";
            return false;
        }
        finally
        {
            if (remoteThread != IntPtr.Zero)
            {
                CloseHandle(remoteThread);
            }

            if (remotePath != IntPtr.Zero)
            {
                VirtualFreeEx(process, remotePath, UIntPtr.Zero, MemRelease);
            }

            CloseHandle(process);
        }
    }

    private static IntPtr FindRemoteModuleBase(int processId, string moduleName)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapModule | Th32csSnapModule32, (uint)processId);
        if (snapshot == new IntPtr(-1))
        {
            return IntPtr.Zero;
        }

        try
        {
            var entry = new MODULEENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<MODULEENTRY32>()
            };
            if (!Module32FirstW(snapshot, ref entry))
            {
                return IntPtr.Zero;
            }

            do
            {
                if (string.Equals(entry.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.modBaseAddr;
                }
            }
            while (Module32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return IntPtr.Zero;
    }

    private static string Win32Error(string operation) =>
        $"{operation} failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        IntPtr process,
        IntPtr address,
        UIntPtr size,
        uint allocationType,
        uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(
        IntPtr process,
        IntPtr address,
        byte[] buffer,
        UIntPtr size,
        out UIntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(
        IntPtr process,
        IntPtr threadAttributes,
        uint stackSize,
        IntPtr startAddress,
        IntPtr parameter,
        uint creationFlags,
        IntPtr threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Module32FirstW(IntPtr snapshot, ref MODULEENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Module32NextW(IntPtr snapshot, ref MODULEENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);
}
