using System.Buffers;
using UiPath.Caching.MessagePack;

namespace UiPath.Caching.Tests.MessagePack;

public class MessagePackAtomicWriteTests
{
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1024)]
    public void Bytes_advanced_before_a_throw_do_not_reach_the_next_stored_value(int threshold)
    {
        var inner = new MessagePackValueSerializer(new MessagePackValueSerializerOptions { CompressionThresholdBytes = threshold });
        var serializer = new ThrowingOnce(inner);
        int[] ids = [.. serializer.FormatIds];

        var act = () => PooledJsonWriter.SerializeValue(serializer, ids, "first");
        act.Should().Throw<InvalidOperationException>();
        using var payload = PooledJsonWriter.SerializeValue(serializer, ids, "second");

        var expected = new ArrayBufferWriter<byte>();
        inner.Serialize("second", expected);
        payload.Memory.Span[1..].ToArray().Should().Equal(expected.WrittenMemory.ToArray());
    }

    private sealed class ThrowingOnce(MessagePackValueSerializer inner) : ICacheValueSerializer
    {
        private bool _thrown;

        public IReadOnlyList<int> FormatIds => inner.FormatIds;

        public int Serialize<T>(T value, IBufferWriter<byte> target)
        {
            if (!_thrown)
            {
                _thrown = true;
                inner.Serialize(value, target);
                throw new InvalidOperationException("the formatter failed after writing");
            }

            return inner.Serialize(value, target);
        }

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) => inner.Deserialize<T>(formatId, source);
    }
}
