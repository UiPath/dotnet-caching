using System.Buffers;

namespace UiPath.Caching.MessagePack;

internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(4096);
    private int _written;

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _written);
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Grow(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Grow(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);

    private void Grow(int sizeHint)
    {
        var needed = _written + Math.Max(sizeHint, 1);
        if (needed <= _buffer.Length)
        {
            return;
        }

        var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _buffer.Length * 2));
        _buffer.AsSpan(0, _written).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = bigger;
    }
}
