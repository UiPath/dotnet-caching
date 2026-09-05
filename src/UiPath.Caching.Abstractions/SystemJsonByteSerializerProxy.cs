using System.Text.Json;

namespace UiPath.Caching;

/// <summary>
/// The default serializer: UTF-8 JSON for every value, byte payloads included, which means base64
/// inside a JSON string. That is the wire format the library has always written, so entries survive
/// an upgrade untouched. Use <see cref="RawByteSerializerProxy"/> to store byte payloads verbatim.
/// </summary>
public class SystemJsonByteSerializerProxy(JsonSerializerOptions? options = null) : IMemorySerializerProxy
{
    /// <summary>
    /// Null goes through JSON like everything else, producing the four-byte <c>null</c> literal. A
    /// null payload would reach StackExchange.Redis as <c>RedisValue.Null</c>, which throws on SADD
    /// and on the multi-field HSET, and silently stores nothing on the single-value paths.
    /// </summary>
    public virtual byte[]? Serialize(object? value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, options);

    /// <summary>
    /// JSON has no memory to lend, so this is <see cref="Serialize"/> wrapped — a byte payload still comes
    /// out as base64 inside a JSON string, keeping the wire format one thing regardless of which member the
    /// tier calls. <see cref="RawByteSerializerProxy"/> is where memory passes through.
    /// </summary>
    public virtual ReadOnlyMemory<byte> SerializeToMemory<T>(T? value) =>
        Serialize(value);

    public virtual T? Deserialize<T>(byte[]? value) =>
        value is null or { Length: 0 } ? default : JsonSerializer.Deserialize<T>(value, options);

    public bool TryDeserialize<T>(string? value, out T? result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            return false;
        }
        try
        {
            result = JsonSerializer.Deserialize<T>(value, options);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    public bool TryDeserialize<T>(object? value, out T? result)
    {
        if (value == null)
        {
            result = default;
            return false;
        }
        try
        {
            switch (value)
            {
                case byte[] { Length: 0 }:
                    result = default;
                    return false;
                case byte[] bytes:
                    result = Deserialize<T>(bytes);
                    return true;
                case JsonElement jsonElement:
                    result = jsonElement.Deserialize<T>(options);
                    return true;
                default:
                    return TryDeserialize(value.ToString() ?? string.Empty, out result);
            }
        }
        catch
        {
            result = default;
            return false;
        }
    }
}
