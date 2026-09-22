using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AntiCheat.DevMcp;

/// <summary>
/// Pins an existing local directory chain without delete sharing. A checked directory cannot
/// be renamed/replaced while the pins live. The final file is opened without following reparses.
/// This is a path primitive; the caller must still enforce its authorized root and file index.
/// </summary>
public sealed class WindowsPathPins : IDisposable
{
    private readonly List<SafeFileHandle> directories = [];
    private bool disposed;

    public string FullPath { get; }

    private WindowsPathPins(string path, bool includeLeafDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows path handles are required.");
        FullPath = Normalize(path);
        if (!includeLeafDirectory && FullPath.Length <= 3)
            throw new ArgumentException("A file path, not a drive root, is required.", nameof(path));
        try
        {
            var lastDirectory = includeLeafDirectory ? FullPath : Path.GetDirectoryName(FullPath)!;
            var current = FullPath[..3];
            PinDirectory(current);
            foreach (var segment in lastDirectory[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                PinDirectory(current);
            }
        }
        catch { Dispose(); throw; }
    }

    public static WindowsPathPins ForFile(string absolutePath) => new(absolutePath, false);
    public static WindowsPathPins ForDirectory(string absolutePath) => new(absolutePath, true);

    /// <summary>Opens a checked existing ordinary file for bounded read operations.</summary>
    public FileStream OpenRead(bool allowConcurrentWrite = false, bool allowAtomicReplace = false) => new(
        OpenFile(Native.GenericRead, Native.ShareRead | (allowConcurrentWrite ? Native.ShareWrite : 0) |
            (allowAtomicReplace ? Native.ShareDelete : 0), Native.OpenExisting), FileAccess.Read);

    /// <summary>Reads at most maximumBytes+1 from the verified handle; metadata length is not the bound.</summary>
    public byte[] ReadBounded(int maximumBytes, bool allowConcurrentWrite = false, bool allowAtomicReplace = false)
    {
        ValidateBound(maximumBytes);
        return ReadBoundedCore(maximumBytes, allowConcurrentWrite, allowAtomicReplace);
    }

    // The registered liquid result reader is the sole extended-bound caller. Ordinary reads
    // and atomic writes keep their 4 MiB cap; use the same handle checks and actual-byte loop.
    internal byte[] ReadRegisteredLiquidDetail() => ReadBoundedCore(16 * 1024 * 1024, false, true);

    private byte[] ReadBoundedCore(int maximumBytes, bool allowConcurrentWrite, bool allowAtomicReplace)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var stream = OpenRead(allowConcurrentWrite, allowAtomicReplace);
                var bytes = new byte[maximumBytes + 1];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = stream.Read(bytes, count, bytes.Length - count);
                    if (read == 0) break;
                    count += read;
                }
                if (count > maximumBytes) throw new InvalidDataException("Development file exceeds its actual byte-read limit.");
                Array.Resize(ref bytes, count);
                return bytes;
            }
            catch (Exception error) when (allowAtomicReplace && watch.ElapsedMilliseconds < 1000 &&
                (error is PathReplacedException || error is Win32Exception native && native.NativeErrorCode is 32 or 33))
            {
                // A publisher may still hold its DELETE handle briefly after renaming. A reader
                // may also have opened the old inode just before it was renamed. Reopen and verify
                // again, with a fixed deadline; never retry invalid reparses/hard links or content.
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>
    /// Writes bounded bytes to a new sibling and renames that same open file handle. The pinned
    /// parent directory chain prevents ancestor replacement while the source handle is renamed.
    /// </summary>
    public void AtomicWrite(byte[] contents, int maximumBytes = 262144)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(contents);
        ValidateBound(maximumBytes);
        if (contents.Length > maximumBytes) throw new InvalidDataException("Development file exceeds its write limit.");
        var parentPath = Path.GetDirectoryName(FullPath)!;
        var temporaryPath = Path.Combine(parentPath, ".dev-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var temporaryPins = ForFile(temporaryPath);
        using var handle = temporaryPins.OpenFile(Native.GenericWrite | Native.Delete, Native.ShareRead, Native.CreateNew);
        using var output = new FileStream(handle, FileAccess.Write, 1, false);
        try
        {
            // Reject an already-invalid destination. A later replacement of the final directory
            // entry is not followed: FileRenameInfo operates on that entry, never its old contents.
            try { using var prior = OpenFile(Native.ReadAttributes, Native.ShareRead | Native.ShareWrite | Native.ShareDelete, Native.OpenExisting); }
            catch (Win32Exception error) when (error.NativeErrorCode == 2) { }
            output.Write(contents);
            output.Flush(flushToDisk: true);
            RenameHandle(handle, FullPath);
        }
        catch (Exception failure)
        {
            // Delete only the inode we created and still hold, never a path that another writer
            // could have replaced. No File.Delete/File.Move fallback is permitted.
            byte delete = 1;
            if (!Native.SetDispositionByHandle(handle, 4, ref delete, 1))
                throw new AggregateException("Atomic publication failed; owned temporary-file cleanup also failed.",
                    failure, new Win32Exception(Marshal.GetLastPInvokeError(), "Temporary file retained: " + temporaryPath));
            throw;
        }
    }

    private static void ValidateBound(int maximumBytes)
    {
        if (maximumBytes is <= 0 or > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Development file bounds must be 1..4194304 bytes.");
    }

    private static void RenameHandle(SafeFileHandle source, string destinationPath)
    {
        // Official Win32 contracts (bindings/implementation written here, no third-party code):
        // https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info
        // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle
        // FileRenameInfo=3 uses BOOLEAN ReplaceIfExists in the first union, not FileRenameInfoEx flags.
        // Marshal.OffsetOf follows HANDLE alignment on x86/x64; do not hard-code the WCHAR offset.
        // The actual supported host rejects RootDirectory + relative FileName with error 87.
        // Use the documented absolute-name form. Every destination ancestor remains pinned with
        // FILE_LIST_DIRECTORY and without FILE_SHARE_DELETE until this same-source-handle rename ends.
        var name = Encoding.Unicode.GetBytes(destinationPath);
        var offset = checked((int)Marshal.OffsetOf<Native.RenameInformation>(nameof(Native.RenameInformation.FileName)));
        var size = Math.Max(Marshal.SizeOf<Native.RenameInformation>(), offset + name.Length + 2);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            var information = new Native.RenameInformation
            { ReplaceUnion = 1, RootDirectory = nint.Zero, FileNameLength = (uint)name.Length };
            Marshal.StructureToPtr(information, buffer, false);
            Marshal.Copy(name, 0, buffer + offset, name.Length);
            var watch = Stopwatch.StartNew();
            while (!Native.SetFileInformationByHandle(source, 3, buffer, (uint)size))
            {
                var error = Marshal.GetLastPInvokeError();
                // Classic FileRenameInfo can report ACCESS_DENIED while an old destination is
                // pending deletion under a concurrent reader, even when that reader shares DELETE.
                // Permission failures remain failures after the same fixed publication deadline.
                if (error is not (5 or 32 or 33) || watch.ElapsedMilliseconds >= 1000)
                    throw new Win32Exception(error, $"Cannot publish the owned temporary file by handle (Win32 {error}).");
                Thread.Sleep(10);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Creates a new ordinary file. Existing evidence is never overwritten.</summary>
    public FileStream CreateNew(bool asynchronous = false) => new(
        OpenFile(Native.GenericWrite, Native.ShareRead, Native.CreateNew, asynchronous),
        FileAccess.Write, 1, asynchronous);

    internal SafeFileHandle OpenFile(uint access, uint share, uint disposition, bool asynchronous = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (FullPath.Length <= 3) throw new ArgumentException("A file path is required.", nameof(FullPath));
        var handle = Native.CreateFileW(FullPath, access, share, nint.Zero, disposition,
            Native.OpenReparsePoint | (asynchronous ? Native.Overlapped : 0), nint.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Cannot open the requested local file.");
        }
        try { Verify(handle, FullPath, directory: false); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private void PinDirectory(string directory)
    {
        // READ_ATTRIBUTES alone does not participate in Windows sharing checks. FILE_LIST_DIRECTORY
        // is required so withholding FILE_SHARE_DELETE actually rejects rename/delete of this ancestor.
        var handle = Native.CreateFileW(directory, Native.ReadAttributes | Native.ListDirectory, Native.ShareRead | Native.ShareWrite,
            nint.Zero, Native.OpenExisting, Native.BackupSemantics | Native.OpenReparsePoint, nint.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Cannot pin the requested local directory.");
        }
        try { Verify(handle, directory, directory: true); directories.Add(handle); }
        catch { handle.Dispose(); throw; }
    }

    private static void Verify(SafeFileHandle handle, string requested, bool directory)
    {
        if (!Native.GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if ((info.FileAttributes & Native.ReparsePointAttribute) != 0)
            throw new IOException("Reparse points are not permitted in development-tool paths.");
        if (((info.FileAttributes & Native.DirectoryAttribute) != 0) != directory || Native.GetFileType(handle) != 1)
            throw new IOException("The opened path has an unexpected file type.");
        if (!directory && info.NumberOfLinks != 1)
            throw new IOException("Development-tool files must have exactly one hard link.");
        var resolved = new StringBuilder(32768);
        var length = Native.GetFinalPathNameByHandleW(handle, resolved, (uint)resolved.Capacity, 0);
        if (length == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (!directory && error is 2 or 3) throw new PathReplacedException();
            throw new Win32Exception(error);
        }
        if (length >= resolved.Capacity) throw new IOException("Resolved path is too long.");
        var actual = resolved.ToString();
        if (!actual.StartsWith("\\\\?\\", StringComparison.Ordinal)) throw new IOException("Unsupported resolved path kind.");
        actual = actual[4..];
        if (!string.Equals(actual.TrimEnd('\\'), requested.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new PathReplacedException();
    }

    public static string Normalize(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        var path = absolutePath.Replace('/', '\\');
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            !Path.IsPathFullyQualified(path) || path.Length > 32000)
            throw new ArgumentException("An ordinary absolute local drive path is required.", nameof(absolutePath));
        while (path.Length > 3 && path.EndsWith('\\')) path = path[..^1];
        foreach (var segment in path[3..].Split('\\'))
        {
            if (path.Length == 3) break;
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new ArgumentException("Ambiguous path segments, traversal and alternate streams are not permitted.", nameof(absolutePath));
            var stem = segment.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                     stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 "123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("DOS device names are not permitted.", nameof(absolutePath));
        }
        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        for (var i = directories.Count - 1; i >= 0; --i) directories[i].Dispose();
        directories.Clear();
    }

    private sealed class PathReplacedException : IOException
    { public PathReplacedException() : base("The opened file no longer resolves to the requested local path.") { } }

    internal static class Native
    {
        internal const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, ReadAttributes = 0x80,
            Delete = 0x10000, ListDirectory = 1;
        internal const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4, CreateNew = 1, OpenExisting = 3, OpenAlways = 4;
        internal const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000, Overlapped = 0x40000000;
        internal const uint DirectoryAttribute = 0x10, ReparsePointAttribute = 0x400;

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInformation
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RenameInformation
        {
            internal uint ReplaceUnion;
            internal nint RootDirectory;
            internal uint FileNameLength;
            internal ushort FileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security,
            uint disposition, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetFileType(SafeFileHandle file);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, nint information, uint size);
        [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetDispositionByHandle(SafeFileHandle file, int informationClass, ref byte delete, uint size);
    }
}
