using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ThermalScope.Services;

/// <summary>
/// Named shared-memory block with an explicit DACL. A default DACL created by an elevated process
/// excludes the (non-elevated) game that has to open it, so the current user, SYSTEM, administrators
/// and app-container processes are granted read/write explicitly. The helper DLL only ever writes
/// FPS numbers into it, so this adds no meaningful attack surface.
/// </summary>
internal sealed class SecureSharedMemory : IDisposable
{
    private const uint PageReadWrite = 0x04;
    private const uint FileMapRead = 0x0004;
    private const uint FileMapWrite = 0x0002;
    private const uint SddlRevision1 = 1;

    private IntPtr _mapping;
    private IntPtr _view;

    private SecureSharedMemory(IntPtr mapping, IntPtr view)
    {
        _mapping = mapping;
        _view = view;
    }

    public static SecureSharedMemory Create(string name, int size)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current user SID is not available.");

        // SYSTEM + Administrators: full. Current user + all app packages: read/write.
        // The low-integrity label lets sandboxed (low IL) games write FPS values too.
        var sddl = $"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{sid})(A;;GRGW;;;AC)S:(ML;;NW;;;LW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SddlRevision1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not build the shared-memory security descriptor.");
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false
            };

            var mapping = CreateFileMappingW(new IntPtr(-1), ref attributes, PageReadWrite, 0, (uint)size, name);
            if (mapping == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileMapping failed.");
            }

            var view = MapViewOfFile(mapping, FileMapRead | FileMapWrite, 0, 0, (UIntPtr)size);
            if (view == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(mapping);
                throw new Win32Exception(error, "MapViewOfFile failed.");
            }

            return new SecureSharedMemory(mapping, view);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    public int ReadInt32(int offset) => Marshal.ReadInt32(_view, offset);

    public float ReadSingle(int offset) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(_view, offset));

    public void WriteInt32(int offset, int value) => Marshal.WriteInt32(_view, offset, value);

    public void Dispose()
    {
        if (_view != IntPtr.Zero)
        {
            UnmapViewOfFile(_view);
            _view = IntPtr.Zero;
        }

        if (_mapping != IntPtr.Zero)
        {
            CloseHandle(_mapping);
            _mapping = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(
        IntPtr file,
        ref SecurityAttributes attributes,
        uint protect,
        uint maximumSizeHigh,
        uint maximumSizeLow,
        string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(
        IntPtr mapping,
        uint desiredAccess,
        uint offsetHigh,
        uint offsetLow,
        UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint revision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);
}
