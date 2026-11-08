using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LegionGoFanControl.Hardware;

/// <summary>Read-only NVMe SMART composite temperature for the disk containing Windows.</summary>
public static class SystemSsdTemperature
{
    public sealed record Reading(int DiskNumber, double CompositeCelsius, double? WarningCelsius = null, double? CriticalCelsius = null);
    private const uint Query = 0x2d1400, Extents = 0x560000;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);
    private static SafeFileHandle Open(string path)
    {
        var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Cannot open system disk for read-only inspection."); }
        return handle;
    }
    private static int Io(SafeFileHandle handle, uint code, byte[]? input, byte[] output, string stage)
    {
        if (!DeviceIoControl(handle, code, input, input?.Length ?? 0, output, output.Length, out int count, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"{stage} failed (Windows error {error}, input {input?.Length ?? 0}, output {output.Length}): {new Win32Exception(error).Message}");
        }
        return count;
    }
    public static Reading Read() => Read(false);
    public static Reading Read(bool includeThresholds)
    {
        string root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? throw new InvalidOperationException("Cannot identify Windows volume.");
        if (root.Length < 2 || root[1] != ':') throw new InvalidOperationException("Windows volume is not a local drive letter.");
        using var volume = Open(@"\\.\" + root[..2]);
        var extents = new byte[1024]; int n = Io(volume, Extents, null, extents, "Windows volume extents query");
        if (n < 32 || BinaryPrimitives.ReadUInt32LittleEndian(extents) != 1) throw new InvalidOperationException("Windows volume does not map unambiguously to one disk.");
        int disk = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(extents.AsSpan(8)));
        using var drive = Open(@"\\.\PhysicalDrive" + disk);
        var descriptor = new byte[1024]; n = Io(drive, Query, new byte[12], descriptor, "System disk device descriptor query");
        if (n < 32 || BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(28)) != 17) throw new InvalidOperationException("Windows system disk is not identified as NVMe.");
        var request = new byte[48 + 512];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 50); // StorageDeviceProtocolSpecificProperty
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), 3); // ProtocolTypeNvme
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), 2); // NVMeDataTypeLogPage
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), 2); // SMART / health information
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(24), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(28), 512);
        var response = new byte[request.Length]; n = Io(drive, Query, request, response, "NVMe SMART query");
        double composite = ParseComposite(response.AsSpan(0, n));
        if (!includeThresholds) return new Reading(disk, composite);
        double? warning = null, critical = null;
        try
        {
            var identify = new byte[48 + 4096];
            BinaryPrimitives.WriteUInt32LittleEndian(identify, 50);
            BinaryPrimitives.WriteUInt32LittleEndian(identify.AsSpan(8), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(identify.AsSpan(12), 1); // NVMe identify
            BinaryPrimitives.WriteUInt32LittleEndian(identify.AsSpan(16), 1); // identify controller
            BinaryPrimitives.WriteUInt32LittleEndian(identify.AsSpan(24), 40);
            BinaryPrimitives.WriteUInt32LittleEndian(identify.AsSpan(28), 4096);
            var result = new byte[identify.Length]; int count = Io(drive, Query, identify, result, "NVMe controller identify query");
            if (count >= 48 && BinaryPrimitives.ReadUInt32LittleEndian(result) == 48 && BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4)) == 48 && BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(8)) == 3 && BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(12)) == 1)
            {
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(24)), length = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(28));
                if (offset >= 40 && length >= 270 && (long)offset + 8 + length <= count)
                {
                    warning = OptionalKelvin(BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan((int)offset + 8 + 266)));
                    critical = OptionalKelvin(BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan((int)offset + 8 + 268)));
                }
            }
        }
        catch (Win32Exception) { } // Some drivers do not expose identify data.
        return new Reading(disk, composite, warning, critical);
    }
    private static double? OptionalKelvin(int kelvin) => kelvin is > 273 and < 394 ? kelvin - 273.15 : null;
    public static double ParseComposite(ReadOnlySpan<byte> response)
    {
        if (response.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(response) != 48 || BinaryPrimitives.ReadUInt32LittleEndian(response[4..]) != 48 || BinaryPrimitives.ReadUInt32LittleEndian(response[8..]) != 3 || BinaryPrimitives.ReadUInt32LittleEndian(response[12..]) != 2)
            throw new InvalidOperationException("Invalid NVMe SMART response descriptor.");
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(response[24..]); uint length = BinaryPrimitives.ReadUInt32LittleEndian(response[28..]);
        if (offset < 40 || length < 512 || offset > int.MaxValue - 8 || (long)offset + 8 + length > response.Length) throw new InvalidOperationException("Invalid NVMe SMART response data bounds.");
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(response.Slice((int)offset + 9, 2));
        double celsius = kelvin - 273.15;
        if (kelvin == 0 || celsius < 0 || celsius > 120) throw new InvalidOperationException("NVMe composite temperature is missing or implausible.");
        return celsius;
    }
}
