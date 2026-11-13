using System.Buffers;
using UiPath.Caching.MessagePack;

namespace UiPath.Caching.Tests.MessagePack;

public class PooledBufferWriterTests
{
    [Fact]
    public void Advance_rejects_a_negative_count()
    {
        using var writer = new PooledBufferWriter();

        var act = () => writer.Advance(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Advance_rejects_a_count_past_the_buffer()
    {
        using var writer = new PooledBufferWriter();
        var capacity = writer.GetSpan().Length;

        var act = () => writer.Advance(capacity + 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Dispose_clears_the_array_it_returns()
    {
        var writer = new PooledBufferWriter();
        var span = writer.GetSpan();
        span.Fill(0x55);
        writer.Advance(span.Length);
        var capacity = span.Length;

        writer.Dispose();

        var rented = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            rented.Should().OnlyContain(b => b == 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [Fact]
    public void Growing_clears_the_array_it_replaces()
    {
        using var writer = new PooledBufferWriter();
        var span = writer.GetSpan();
        span.Fill(0x55);
        writer.Advance(span.Length);
        var capacity = span.Length;

        writer.GetSpan(1);

        var rented = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            rented.Should().OnlyContain(b => b == 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
