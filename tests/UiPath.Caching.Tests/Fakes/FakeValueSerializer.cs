using System.Buffers;

namespace UiPath.Caching.Tests.Fakes;

internal sealed class FakeValueSerializer(params int[] ids) : ICacheValueSerializer
{
    public IReadOnlyList<int> FormatIds { get; } = ids;

    public int Serialize<T>(T value, IBufferWriter<byte> target) => throw new NotSupportedException();

    public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) => throw new NotSupportedException();
}

internal sealed class DisposableFakeValueSerializer(params int[] ids) : ICacheValueSerializer, IDisposable
{
    public int Disposed { get; private set; }

    public IReadOnlyList<int> FormatIds { get; } = ids;

    public int Serialize<T>(T value, IBufferWriter<byte> target) => throw new NotSupportedException();

    public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) => throw new NotSupportedException();

    public void Dispose() => Disposed++;
}
