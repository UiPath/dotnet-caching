namespace UiPath.Caching.Tests.Fakes;

/// <summary>The built-in JSON serializer, counting deserializations; each can be slowed to widen a race.</summary>
internal sealed class CountingSerializer : ISerializerProxy<byte[]>
{
    private readonly SystemJsonByteSerializerProxy _inner = new();
    private int _deserialized;

    public int Deserialized => Volatile.Read(ref _deserialized);

    public TimeSpan Delay { get; init; }

    public byte[]? Serialize(object? value) => _inner.Serialize(value);

    public T? Deserialize<T>(byte[]? value)
    {
        Interlocked.Increment(ref _deserialized);
        if (Delay > TimeSpan.Zero)
        {
            Thread.Sleep(Delay);
        }

        return _inner.Deserialize<T>(value);
    }

    public bool TryDeserialize<T>(string? value, out T? result) => _inner.TryDeserialize(value, out result);

    public bool TryDeserialize<T>(object? value, out T? result) => _inner.TryDeserialize(value, out result);
}
