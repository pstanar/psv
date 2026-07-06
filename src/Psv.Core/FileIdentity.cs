using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Psv.Core;

/// <summary>
/// Cross-platform file identity (device/volume + inode/file-index) - a strictly stronger "is this
/// really the same file" signal than comparing creation timestamps, which several Linux
/// filesystems and network shares don't track reliably (see <see cref="FileTailWatcher"/>).
/// <see cref="TryRead"/> returns a struct with <see cref="IsValid"/> == false when the platform
/// isn't supported or the underlying syscall fails, so the caller can fall back to the
/// creation-time heuristic. Implemented and verified (native Windows + WSL2/ext4 Linux) for
/// Windows and Linux; other platforms fall back to the heuristic.
/// </summary>
internal readonly struct FileIdentity : IEquatable<FileIdentity>
{
    private readonly ulong _device;
    private readonly ulong _fileIndex;

    private FileIdentity(ulong device, ulong fileIndex)
    {
        _device = device;
        _fileIndex = fileIndex;
        IsValid = true;
    }

    public bool IsValid { get; }

    public static FileIdentity TryRead(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return TryReadWindows(path);
            }

            if (OperatingSystem.IsLinux())
            {
                return TryReadLinux(path);
            }

            return default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return default;
        }
    }

    public bool Equals(FileIdentity other) =>
        IsValid && other.IsValid && _device == other._device && _fileIndex == other._fileIndex;

    public override bool Equals(object? obj) => obj is FileIdentity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_device, _fileIndex);

    // --- Windows: GetFileInformationByHandle, a Win32 ABI that has been stable since Windows 2000. ---

    private static FileIdentity TryReadWindows(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info))
        {
            return default;
        }

        ulong fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return new FileIdentity(info.VolumeSerialNumber, fileIndex);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint DwLowDateTime;
        public uint DwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    // --- Linux: statx(2), a stable, explicitly-versioned UAPI struct (unlike legacy stat/stat64,
    // whose layout varies by glibc version and word size). Available since kernel 4.11 / glibc
    // 2.28, which covers every currently-supported distro this app targets. ---

    private const int AtFdCwd = -100;
    private const uint StatxMaskIno = 0x100;

    private static FileIdentity TryReadLinux(string path)
    {
        int result = statx(AtFdCwd, path, 0, StatxMaskIno, out var statxBuf);
        if (result != 0)
        {
            return default;
        }

        ulong device = ((ulong)statxBuf.DevMajor << 32) | statxBuf.DevMinor;
        return new FileIdentity(device, statxBuf.Ino);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long TvSec;
        public uint TvNsec;
        public int Reserved;
    }

    // The kernel guarantees sizeof(struct statx) == 256 bytes, with trailing fields reserved for
    // future growth. Size = 256 makes the marshaler pad to that regardless of which fields are
    // declared below, so statx() never writes past the buffer we hand it.
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct Statx
    {
        public uint Mask;
        public uint Blksize;
        public ulong Attributes;
        public uint Nlink;
        public uint Uid;
        public uint Gid;
        public ushort Mode;
        public ushort Spare0;
        public ulong Ino;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public StatxTimestamp Atime;
        public StatxTimestamp Btime;
        public StatxTimestamp Ctime;
        public StatxTimestamp Mtime;
        public uint RdevMajor;
        public uint RdevMinor;
        public uint DevMajor;
        public uint DevMinor;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(
        int dirfd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname,
        int flags,
        uint mask,
        out Statx statxbuf);
}
