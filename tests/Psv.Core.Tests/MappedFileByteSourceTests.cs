using System.Text;

namespace Psv.Core.Tests;

public class MappedFileByteSourceTests
{
    [Fact]
    public void ReadReturnsExactFileContent()
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] expected = Encoding.UTF8.GetBytes("hello world, this is a test file\n");
            File.WriteAllBytes(path, expected);

            using var source = new MappedFileByteSource(path);
            Assert.Equal(expected.Length, source.Length);

            byte[] actual = new byte[expected.Length];
            int read = source.Read(0, actual);
            Assert.Equal(expected.Length, read);
            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadPartialRangeReturnsCorrectSlice()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "0123456789");
            using var source = new MappedFileByteSource(path);

            byte[] buffer = new byte[4];
            int read = source.Read(3, buffer);
            Assert.Equal(4, read);
            Assert.Equal("3456", Encoding.ASCII.GetString(buffer));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EmptyFileHasZeroLengthAndReadsNothing()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, []);
            using var source = new MappedFileByteSource(path);
            Assert.Equal(0, source.Length);

            byte[] buffer = new byte[10];
            Assert.Equal(0, source.Read(0, buffer));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RemapPicksUpGrowthAppendedByAnotherHandle()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "first-");
            using var source = new MappedFileByteSource(path);
            Assert.Equal(6, source.Length);

            using (var appendStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(appendStream))
            {
                writer.Write("second");
            }

            source.Remap();
            Assert.Equal(12, source.Length);

            byte[] buffer = new byte[12];
            source.Read(0, buffer);
            Assert.Equal("first-second", Encoding.ASCII.GetString(buffer));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RemapFromEmptyToNonEmptyWorks()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, []);
            using var source = new MappedFileByteSource(path);
            Assert.Equal(0, source.Length);

            File.WriteAllText(path, "now has content");
            source.Remap();

            Assert.Equal(15, source.Length);
            byte[] buffer = new byte[15];
            source.Read(0, buffer);
            Assert.Equal("now has content", Encoding.ASCII.GetString(buffer));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
