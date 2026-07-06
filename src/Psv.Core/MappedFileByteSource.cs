using System.IO.MemoryMappedFiles;

namespace Psv.Core;

/// <summary>
/// 64-bit-only memory-mapped file access (plan §2.1). Opens with sharing that tolerates a file
/// being written to concurrently, and exposes two recovery paths for the live-tail watcher:
/// <see cref="Remap"/> for growth of the same underlying file, and <see cref="Reopen"/> for
/// rotation, where the path now refers to a different file entirely (the common Windows-safe
/// rotation pattern is rename-based, not in-place truncation — Windows outright refuses to
/// shrink a file that has an active memory-mapped section, discovered while testing milestone 5).
/// Reads go through a single cached <see cref="MemoryMappedViewAccessor"/> spanning the whole
/// mapping — creating a view per read costs a kernel mapping operation each time, and reads
/// happen ~50+ times per rendered frame. Thread-safe: the tail watcher remaps/reopens on a
/// background thread while the UI thread concurrently reads, guarded by a
/// <see cref="ReaderWriterLockSlim"/> — many concurrent reads, exclusive during remap/reopen.
/// </summary>
public sealed class MappedFileByteSource : IByteSource, IDisposable
{
    private readonly string _path;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private FileStream _fileStream;
    private MemoryMappedFile? _mappedFile;
    private MemoryMappedViewAccessor? _accessor;
    private long _length;

    public MappedFileByteSource(string path)
    {
        _path = path;
        _fileStream = OpenFile(path);
        _length = _fileStream.Length;
        CreateMapping();
    }

    public long Length
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _length;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        _lock.EnterReadLock();
        try
        {
            if (_accessor is null || offset >= _length || buffer.IsEmpty)
            {
                return 0;
            }

            long available = _length - offset;
            int toRead = (int)Math.Min(buffer.Length, available);
            _accessor.SafeMemoryMappedViewHandle.ReadSpan((ulong)offset, buffer[..toRead]);
            return toRead;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Picks up growth of the same underlying file (same identity, larger EOF).</summary>
    public void Remap()
    {
        long currentLength = _fileStream.Length;

        _lock.EnterWriteLock();
        try
        {
            if (currentLength == _length)
            {
                return;
            }

            DisposeMapping();
            _length = currentLength;
            CreateMapping();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Closes the current file handle and mapping and opens a fresh one against the same path —
    /// required when the path now refers to a different file (rotation via rename, or a
    /// truncate-in-place on platforms where that's possible while mapped). <see cref="Remap"/>
    /// alone can't handle this: it only re-checks the length of the *existing* handle, which
    /// after a rename still refers to the old, now-unlinked file.
    /// </summary>
    public void Reopen()
    {
        _lock.EnterWriteLock();
        try
        {
            DisposeMapping();
            _fileStream.Dispose();
            _fileStream = OpenFile(_path);
            _length = _fileStream.Length;
            CreateMapping();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        _lock.EnterWriteLock();
        try
        {
            DisposeMapping();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        _fileStream.Dispose();
        _lock.Dispose();
    }

    private static FileStream OpenFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private void CreateMapping()
    {
        if (_length == 0)
        {
            _mappedFile = null;
            _accessor = null;
            return;
        }

        _mappedFile = MemoryMappedFile.CreateFromFile(
            _fileStream,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.Read,
            HandleInheritability.None,
            leaveOpen: true);
        _accessor = _mappedFile.CreateViewAccessor(0, _length, MemoryMappedFileAccess.Read);
    }

    private void DisposeMapping()
    {
        _accessor?.Dispose();
        _accessor = null;
        _mappedFile?.Dispose();
        _mappedFile = null;
    }
}
