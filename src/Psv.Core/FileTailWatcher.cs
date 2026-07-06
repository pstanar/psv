namespace Psv.Core;

/// <summary>
/// Watches a file for growth or truncation/replacement (plan §2.5). Uses a
/// <see cref="FileSystemWatcher"/> as the primary signal with a polling timer fallback, since FSW
/// is known to be unreliable on some network filesystems and Linux configurations. Both signals
/// funnel through the same check, so whichever notices first wins — no double-processing.
/// </summary>
public sealed class FileTailWatcher : IDisposable
{
    private readonly string _path;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _pollTimer;
    private readonly Lock _gate = new();
    private long _lastKnownLength;
    private DateTime _lastKnownCreationTimeUtc;
    private FileIdentity _lastKnownIdentity;
    private bool _disposed;

    public event Action? FileGrew;

    public event Action? FileReplacedOrTruncated;

    public FileTailWatcher(string path, TimeSpan pollInterval)
    {
        _path = path;

        var info = new FileInfo(path);
        _lastKnownLength = info.Exists ? info.Length : 0;
        _lastKnownCreationTimeUtc = info.Exists ? info.CreationTimeUtc : DateTime.MinValue;
        _lastKnownIdentity = info.Exists ? FileIdentity.TryRead(path) : default;

        _watcher = TryCreateWatcher(path);
        _pollTimer = new Timer(_ => CheckForChanges(), null, pollInterval, pollInterval);
    }

    private FileSystemWatcher? TryCreateWatcher(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is null)
            {
                return null;
            }

            var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            watcher.Changed += (_, _) => CheckForChanges();
            watcher.Created += (_, _) => CheckForChanges();
            watcher.Renamed += (_, _) => CheckForChanges();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void CheckForChanges()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var info = new FileInfo(_path);
            if (!info.Exists)
            {
                return;
            }

            long currentLength = info.Length;
            DateTime currentCreationTimeUtc = info.CreationTimeUtc;
            FileIdentity currentIdentity = FileIdentity.TryRead(_path);

            // Where the OS exposes a real file identity (device + inode/file-index), that's a
            // strictly stronger "is this really the same file" signal than creation-time, which
            // several Linux filesystems and network shares don't track reliably - a rename-over
            // rotation there can leave the reported creation time unchanged even though the
            // underlying file is brand new.
            bool identityChanged = currentIdentity.IsValid && _lastKnownIdentity.IsValid
                ? !currentIdentity.Equals(_lastKnownIdentity)
                : currentCreationTimeUtc != _lastKnownCreationTimeUtc;

            bool replaced = identityChanged || currentLength < _lastKnownLength;
            bool grew = !replaced && currentLength > _lastKnownLength;

            _lastKnownLength = currentLength;
            _lastKnownCreationTimeUtc = currentCreationTimeUtc;
            _lastKnownIdentity = currentIdentity;

            if (replaced)
            {
                FileReplacedOrTruncated?.Invoke();
            }
            else if (grew)
            {
                FileGrew?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }

        _pollTimer.Dispose();
    }
}
