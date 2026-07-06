namespace Psv.Core.Tests;

/// <summary>Growable in-memory <see cref="IByteSource"/> for simulating file growth/truncation in tests.</summary>
internal sealed class MutableByteSource(byte[] initial) : IByteSource
{
    private readonly Lock _lock = new();
    private byte[] _data = initial;

    public long Length
    {
        get
        {
            lock (_lock)
            {
                return _data.Length;
            }
        }
    }

    public int Read(long offset, Span<byte> buffer)
    {
        lock (_lock)
        {
            if (offset >= _data.Length)
            {
                return 0;
            }

            int available = (int)Math.Min(buffer.Length, _data.Length - offset);
            _data.AsSpan((int)offset, available).CopyTo(buffer);
            return available;
        }
    }

    public void Append(byte[] more)
    {
        lock (_lock)
        {
            var combined = new byte[_data.Length + more.Length];
            _data.CopyTo(combined, 0);
            more.CopyTo(combined, _data.Length);
            _data = combined;
        }
    }

    public void Replace(byte[] newData)
    {
        lock (_lock)
        {
            _data = newData;
        }
    }
}
