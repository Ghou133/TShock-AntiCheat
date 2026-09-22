using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AntiCheat.DevMcp.Tests;

internal static class NativeTestLinks
{
    // Test-only NTFS fixture creation. API/layout references:
    // https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createhardlinkw
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_reparse_data_buffer
    // https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_set_reparse_point
    public static void CreateHardLink(string alias, string original)
    {
        if (!CreateHardLinkW(alias, original, nint.Zero)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public static void CreateJunction(string junction, string target)
    {
        Directory.CreateDirectory(junction);
        try
        {
            using var directory = CreateFileW(junction, 0x40000000, 7, nint.Zero, 3, 0x02200000, nint.Zero);
            if (directory.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
            byte[] print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
            byte[] data = new byte[16 + substitute.Length + 2 + print.Length + 2];
            BinaryPrimitives.WriteUInt32LittleEndian(data, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 8)));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), checked((ushort)substitute.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), checked((ushort)(substitute.Length + 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), checked((ushort)print.Length));
            substitute.CopyTo(data, 16); print.CopyTo(data, 16 + substitute.Length + 2);
            if (!DeviceIoControl(directory, 0x000900A4, data, (uint)data.Length, nint.Zero, 0, out _, nint.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The actual NTFS junction fixture could not be created.");
        }
        catch { Directory.Delete(junction); throw; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, nint security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, uint inputSize,
        nint output, uint outputSize, out uint returned, nint overlapped);
}
