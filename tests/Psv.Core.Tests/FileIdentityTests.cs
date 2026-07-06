namespace Psv.Core.Tests;

public class FileIdentityTests
{
    [Fact]
    public void ExistingFileHasAValidIdentityOnThisPlatform()
    {
        // FileIdentity is only implemented (and thus IsValid) on Windows and Linux today; other
        // platforms are expected to fall back to FileTailWatcher's creation-time heuristic.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string path = Path.GetTempFileName();
        try
        {
            var identity = FileIdentity.TryRead(path);
            Assert.True(identity.IsValid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ANonExistentPathHasNoValidIdentity()
    {
        string path = Path.Combine(Path.GetTempPath(), $"psv-does-not-exist-{Guid.NewGuid():N}");
        Assert.False(FileIdentity.TryRead(path).IsValid);
    }

    [Fact]
    public void TwoDifferentFilesHaveDifferentIdentities()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string pathA = Path.GetTempFileName();
        string pathB = Path.GetTempFileName();
        try
        {
            var identityA = FileIdentity.TryRead(pathA);
            var identityB = FileIdentity.TryRead(pathB);

            Assert.True(identityA.IsValid);
            Assert.True(identityB.IsValid);
            Assert.NotEqual(identityA, identityB);
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    [Fact]
    public void RenamingAFilePreservesItsIdentity()
    {
        // This is the crux of what FileTailWatcher relies on: a rotation tool that renames the
        // live file away keeps the *same* underlying file (same inode/file-index) at its new
        // path - it's a fresh file created at the *original* path that must be seen as different.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string original = Path.GetTempFileName();
        string renamed = original + ".renamed";
        try
        {
            var beforeRename = FileIdentity.TryRead(original);
            Assert.True(beforeRename.IsValid);

            File.Move(original, renamed);
            var afterRename = FileIdentity.TryRead(renamed);
            Assert.Equal(beforeRename, afterRename);

            File.WriteAllText(original, "fresh file at the old path");
            var freshFileAtOriginalPath = FileIdentity.TryRead(original);
            Assert.True(freshFileAtOriginalPath.IsValid);
            Assert.NotEqual(beforeRename, freshFileAtOriginalPath);
        }
        finally
        {
            File.Delete(original);
            File.Delete(renamed);
        }
    }

    [Fact]
    public void AppendingToAFileDoesNotChangeItsIdentity()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string path = Path.GetTempFileName();
        try
        {
            var before = FileIdentity.TryRead(path);

            using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(append))
            {
                writer.Write("more content\n");
            }

            var after = FileIdentity.TryRead(path);
            Assert.Equal(before, after);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
