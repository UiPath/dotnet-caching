using System.Text.Json;

namespace UiPath.Caching;

public class SystemJsonSerializerProxy : ISerializerProxy<RedisValue>
{
    private readonly JsonSerializerOptions? _options;

    public SystemJsonSerializerProxy(JsonSerializerOptions? options = null) =>
        _options = options;

    public RedisValue Serialize(object? value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, _options);

    public T? Deserialize<T>(RedisValue value)
    {
        if (value.IsNullOrEmpty)
        {
            return default;
        }

        ReadOnlyMemory<byte> payload = value;
        return JsonSerializer.Deserialize<T>(payload.Span, _options);
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
            result = JsonSerializer.Deserialize<T>(value, _options);
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
            if(value is JsonElement jsonElement)
            {
                result = jsonElement.Deserialize<T>(_options);
                return true;
            }
            else
            {
                var text = value.ToString() ?? string.Empty;
                return TryDeserialize(text, out result);
            }
        }
        catch
        {
            result = default;
            return false;
        }
    }
}
