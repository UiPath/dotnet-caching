namespace UiPath.Caching.Distributed;

/// <summary>Adapts an <c>ISerializerProxy&lt;byte[]&gt;</c> onto the Redis pipeline for the distributed cache's dedicated instance.</summary>
internal sealed class RedisValueSerializerProxy(ISerializerProxy<byte[]> inner) : ISerializerProxy<RedisValue>
{
    public RedisValue Serialize(object? value) =>
        inner.Serialize(value);

    public T? Deserialize<T>(RedisValue value) =>
        value.IsNullOrEmpty ? default : inner.Deserialize<T>(value);

    public bool TryDeserialize<T>(string? value, out T? result) =>
        inner.TryDeserialize(value, out result);

    public bool TryDeserialize<T>(object? value, out T? result) =>
        inner.TryDeserialize(value, out result);
}
