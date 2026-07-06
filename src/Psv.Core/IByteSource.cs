namespace Psv.Core;

public interface IByteSource
{
    long Length { get; }

    /// <summary>
    /// Reads up to <paramref name="buffer"/>.Length bytes starting at <paramref name="offset"/>.
    /// Returns the number of bytes actually read; 0 only at or past end of source.
    /// </summary>
    int Read(long offset, Span<byte> buffer);
}
