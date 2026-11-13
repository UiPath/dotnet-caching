using System.Buffers;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>Owns its ids and throws on every read, as a serializer does on input it cannot parse; <see cref="Failure"/> picks what it throws.</summary>
internal sealed class ThrowingValueSerializer(params int[] ids) : ICacheValueSerializer
{
    public Exception? Failure { get; init; }

    public IReadOnlyList<int> FormatIds { get; } = ids.Length == 0 ? [0x10] : ids;

    public int Serialize<T>(T value, IBufferWriter<byte> target) => throw new NotSupportedException();

    public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) => throw Failure ?? new InvalidDataException("corrupt");
}
