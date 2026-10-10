using System.Runtime.InteropServices;

namespace ThermalScope.Services;

/// <summary>One sample of the CPU values MSI Afterburner publishes.</summary>
internal sealed record AfterburnerCpu(
    double? Temperature,
    IReadOnlyList<(string Name, double Value)> ThreadTemperatures,
    double? ClockMegahertz,
    double? PowerWatts);

/// <summary>
/// Reads the hardware monitor that MSI Afterburner (and RTSS) publish in the named shared-memory block
/// "MAHMSharedMemory". Afterburner reads the real CPU die sensors with its own signed driver, so this is a
/// legitimate, driver-free (for ThermalScope) source of CPU temperature, power and clock. Nothing is read
/// unless Afterburner is running, and stale data (Afterburner paused or hung) is ignored.
/// </summary>
internal static class AfterburnerReader
{
    private const string MappingName = "MAHMSharedMemory";
    private const uint Signature = 0x4D41484D; // 'MAHM'
    private const uint MinVersion = 0x00020000;
    private const int FileMapRead = 0x0004;
    private const int MaxAgeSeconds = 15;

    // Header: signature, version, headerSize, numEntries, entrySize, time(uint32 at 20).
    // Entry: name[260] @0, units[260] @260, ..., data(float) @1300, ..., gpu index(uint32) @1316.
    private const int NameOffset = 0;
    private const int DataOffset = 1300;
    private const int GpuOffset = 1316;
    private const int MinEntrySize = 1324;
    private const uint NotPerGpu = 0xFFFFFFFF;

    public static AfterburnerCpu? TryRead()
    {
        var mapping = OpenFileMappingW(FileMapRead, false, MappingName);
        if (mapping == IntPtr.Zero)
        {
            return null;
        }

        var view = IntPtr.Zero;
        try
        {
            view = MapViewOfFile(mapping, FileMapRead, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero)
            {
                return null;
            }

            if ((uint)Marshal.ReadInt32(view, 0) != Signature || (uint)Marshal.ReadInt32(view, 4) < MinVersion)
            {
                return null;
            }

            var headerSize = Marshal.ReadInt32(view, 8);
            var count = Marshal.ReadInt32(view, 12);
            var entrySize = Marshal.ReadInt32(view, 16);
            var time = (uint)Marshal.ReadInt32(view, 20);
            if (entrySize < MinEntrySize || headerSize < 24 || count <= 0 || count > 1024)
            {
                return null;
            }

            var age = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - time);
            if (age > MaxAgeSeconds)
            {
                return null;
            }

            double? temperature = null, clock = null, power = null;
            var threads = new List<(string, double)>();
            for (var i = 0; i < count; i++)
            {
                var entry = headerSize + i * entrySize;
                var name = ReadName(view, entry + NameOffset);
                if (Marshal.ReadInt32(view, entry + GpuOffset) != unchecked((int)NotPerGpu) && !IsCpuThread(name))
                {
                    continue;
                }

                var value = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(view, entry + DataOffset));
                if (!float.IsFinite(value))
                {
                    continue;
                }

                switch (name)
                {
                    case "CPU temperature" when value is > 0 and < 150:
                        temperature = value;
                        break;
                    case "CPU clock" when value > 0:
                        clock = value;
                        break;
                    case "CPU power" when value > 0:
                        power = value;
                        break;
                    default:
                        if (IsCpuThread(name) && value is > 0 and < 150)
                        {
                            threads.Add((name, value));
                        }

                        break;
                }
            }

            return temperature is null && threads.Count == 0 ? null : new AfterburnerCpu(temperature, threads, clock, power);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (view != IntPtr.Zero)
            {
                UnmapViewOfFile(view);
            }

            CloseHandle(mapping);
        }
    }

    // The block belongs to another process: never trust it to be NUL-terminated, stop at the field's 260 bytes.
    private static string ReadName(IntPtr view, int offset)
    {
        const int fieldBytes = 260;
        var bytes = new byte[fieldBytes];
        var length = 0;
        while (length < fieldBytes)
        {
            var b = Marshal.ReadByte(view, offset + length);
            if (b == 0)
            {
                break;
            }

            bytes[length++] = b;
        }

        return System.Text.Encoding.ASCII.GetString(bytes, 0, length);
    }

    // "CPU1 temperature" .. "CPU12 temperature": per-thread die sensors (published with a GPU index, not 0xFFFFFFFF).
    private static bool IsCpuThread(string? name) =>
        name is not null && name.StartsWith("CPU", StringComparison.Ordinal) &&
        name.EndsWith(" temperature", StringComparison.Ordinal) && name.Length > "CPU temperature".Length;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenFileMappingW(int access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, int access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
