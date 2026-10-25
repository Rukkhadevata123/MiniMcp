using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MiniMcp.Recovery;

internal readonly record struct RecoveryFileIdentity(ulong Device, ulong File);

// Native directory creation is exclusive. Handle metadata rejects links/special files before file I/O.
// Linux statx uses its fixed UAPI layout, not architecture-dependent libc struct stat offsets:
// https://github.com/torvalds/linux/blob/master/include/uapi/linux/stat.h
internal static class RecoveryFileSystem
{
    public static bool CreateDirectory(string path)
    {
        CheckPlatform();
        var created = OperatingSystem.IsWindows() ? CreateDirectoryWindows(WindowsPath(path), IntPtr.Zero) :
            OperatingSystem.IsLinux() ? MakeDirectory(path, 0x1c0) == 0 : throw new PlatformNotSupportedException(); // 0700
        if (created) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == (OperatingSystem.IsWindows() ? 183 : 17)) return false;
        throw Failure("Create recovery directory", error);
    }

    public static FileStream OpenFile(string path, FileMode mode) => OpenFile(path, mode, FileAccess.ReadWrite);
    public static FileStream OpenRead(string path) => OpenFile(path, FileMode.Open, FileAccess.Read);
    public static FileStream OpenSharedRead(string path) => OpenFile(path, FileMode.Open, FileAccess.Read, windowsShare: 1); // FILE_SHARE_READ only

    private static FileStream OpenFile(string path, FileMode mode, FileAccess access, uint windowsShare = 0)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            var disposition = mode switch { FileMode.CreateNew => 1u, FileMode.Open => 3u, FileMode.OpenOrCreate => 4u, _ => throw new ArgumentException("Unsupported recovery file mode.") };
            handle = CreateFileWindows(WindowsPath(path), access == FileAccess.Read ? 0x80000000 : 0xc0000000, windowsShare, IntPtr.Zero, disposition, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) { var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw Failure("Open recovery file", error); }
        }
        else if (OperatingSystem.IsLinux())
        {
            var flags = mode switch { FileMode.CreateNew => 0x40 | 0x80, FileMode.Open => 0, FileMode.OpenOrCreate => 0x40, _ => throw new ArgumentException("Unsupported recovery file mode.") };
            handle = OpenLinux(path, flags | (access == FileAccess.Read ? 0 : 2) | 0x800 | 0x20000 | 0x80000, 0x180); // RDONLY/RDWR, NONBLOCK, NOFOLLOW, CLOEXEC; 0600
        }
        else throw new PlatformNotSupportedException();
        try
        {
            _ = Identity(handle, directory: false);
            return new FileStream(handle, access, 4096, isAsync: false);
        }
        catch { handle.Dispose(); throw; }
    }

    public static RecoveryFileIdentity FileIdentity(FileStream stream) => Identity(stream.SafeFileHandle, directory: false);

    public static RecoveryFileIdentity DirectoryIdentity(string path)
    {
        using var handle = OpenDirectory(path);
        return Identity(handle, directory: true);
    }

    public static void Lock(FileStream stream)
    {
        // Windows OpenFile already denies sharing; Linux locks coordinate cooperating MiniMcp writers.
        if (OperatingSystem.IsLinux() && Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 2 | 4) != 0)
            throw Failure("Lock recovery storage", Marshal.GetLastPInvokeError());
    }

    public static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var handle = OpenDirectory(path);
        if (Fsync(handle.DangerousGetHandle().ToInt32()) != 0) throw Failure("Flush recovery directory", Marshal.GetLastPInvokeError());
    }

    // Callers verify two regular files in the same directory/device before this rename.
    // No cross-volume copy/delete fallback. Errors after the call begins are treated as uncertain.
    public static void ReplaceFile(string source, string destination)
    {
        CheckPlatform();
        var replaced = OperatingSystem.IsWindows()
            ? MoveFileWindows(WindowsPath(source), WindowsPath(destination), 1 | 8) // REPLACE_EXISTING | WRITE_THROUGH
            : Rename(source, destination) == 0;
        if (!replaced) throw Failure("Replace recovery manifest", Marshal.GetLastPInvokeError());
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFileWindows(WindowsPath(path), 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); // BACKUP_SEMANTICS | OPEN_REPARSE_POINT
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw Failure("Open recovery directory", error);
        }
        if (OperatingSystem.IsLinux()) return OpenLinux(path, 0x10000 | 0x800 | 0x20000 | 0x80000, 0);
        throw new PlatformNotSupportedException();
    }

    private static SafeFileHandle OpenLinux(string path, int flags, uint mode)
    {
        CheckPlatform();
        var descriptor = Open(path, flags, mode);
        if (descriptor < 0) throw Failure("Open recovery entry", Marshal.GetLastPInvokeError());
        if (descriptor == 0)
        {
            var duplicate = DuplicateDescriptor(descriptor, 1030, 3); // F_DUPFD_CLOEXEC; SafeFileHandle treats zero as invalid.
            var error = Marshal.GetLastPInvokeError();
            _ = Close(descriptor);
            if (duplicate < 0) throw Failure("Duplicate recovery descriptor", error);
            descriptor = duplicate;
        }
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static RecoveryFileIdentity Identity(SafeFileHandle handle, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (GetFileType(handle) != 1 || !GetFileInformation(handle, out var info))
                throw new IOException("Recovery storage requires identifiable disk entries.");
            var attributes = (FileAttributes)info.Attributes;
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != directory || (!directory && info.Links != 1))
                throw new IOException("Recovery storage rejects links, hard-linked files and non-regular entries.");
            var index = ((ulong)info.IndexHigh << 32) | info.IndexLow;
            if (index == 0) throw new IOException("Recovery storage requires stable nonzero file identities.");
            return new(info.Volume, index);
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        const uint requested = 0x105; // STATX_TYPE | STATX_NLINK | STATX_INO
        try
        {
            if (Statx(handle.DangerousGetHandle().ToInt32(), "", 0x1000, requested, out var status) != 0)
                throw Failure("Inspect recovery entry", Marshal.GetLastPInvokeError());
            if ((status.Mask & requested) != requested || status.Inode == 0 || (status.Mode & 0xf000) != (directory ? 0x4000 : 0x8000) || (!directory && status.Links != 1))
                throw new IOException("Recovery storage requires identifiable regular files/directories without hard links.");
            return new(((ulong)status.DeviceMajor << 32) | status.DeviceMinor, status.Inode);
        }
        catch (EntryPointNotFoundException error) { throw new IOException("Recovery storage requires Linux statx support.", error); }
    }

    private static string WindowsPath(string path) => path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path :
        path.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + path[2..] : "\\\\?\\" + path;
    private static void CheckPlatform()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Recovery persistence supports Windows and Linux x64/arm64 native interfaces.");
    }
    private static IOException Failure(string operation, int code) => new($"{operation} failed (native error {code}).", new Win32Exception(code));

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryWindows(string path, IntPtr attributes);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileWindows(string path, uint access, uint share, IntPtr attributes, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFileWindows(string source, string destination, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformation(SafeFileHandle handle, out WindowsInformation information);
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)] private static extern int MakeDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)] private static extern int Flock(int descriptor, int operation);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int DuplicateDescriptor(int descriptor, int command, int minimum);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int Close(int descriptor);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int Statx(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatx status);
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
}
