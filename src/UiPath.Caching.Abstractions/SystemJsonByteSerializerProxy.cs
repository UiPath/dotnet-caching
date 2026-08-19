using System.Text.Json;

namespace UiPath.Caching;

/// <summary>JSON serializer over <c>byte[]</c>: byte payloads pass through raw, everything else is UTF-8 JSON; the type argument decides, no format sniffing.</summary>
public class SystemJsonByteSerializerProxy(JsonSerializerOptions? options = null) : ISerializerProxy<byte[]>
{
    public byte[]? Serialize(object? value) => value switch
    {
        null => null,
        byte[] bytes => bytes,
        ReadOnlyMemory<byte> memory => memory.ToArray(),
        _ => JsonSerializer.SerializeToUtf8Bytes(value, options),
    };

    public T? Deserialize<T>(byte[]? value)
    {
        if (value is null || value.Length == 0)
        {
            return default;
        }
        if (value is T bytes)
        {
            return bytes;
        }
        return JsonSerializer.Deserialize<T>(value, options);
    }

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
                case byte[] bytes when bytes is T typed:
                    result = typed;
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
