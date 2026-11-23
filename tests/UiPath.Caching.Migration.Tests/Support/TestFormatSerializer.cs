using System.Buffers;
using System.Text.Json;

namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>A value serializer standing in for MessagePack: JSON bytes under the header id 0x02, which the library prefixes.</summary>
internal sealed class TestFormatSerializer : ICacheValueSerializer
{
    public const int Id = 0x02;

    public IReadOnlyList<int> FormatIds { get; } = [Id];

    public int Serialize<T>(T value, IBufferWriter<byte> target)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        target.Write(bytes);
        return Id;
    }

    public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) =>
        formatId == Id ? JsonSerializer.Deserialize<T>(source.ToArray()) : throw new InvalidOperationException($"Not format {formatId}.");
}
