using System.Text.Json;

namespace UiPath.Platform.Caching;

public class SystemJsonSerializerProxy : ISerializerProxy<RedisValue>
{
    private readonly JsonSerializerOptions? _options;

    public SystemJsonSerializerProxy(JsonSerializerOptions? options = null) =>
        _options = options;

    public RedisValue Serialize(object? value) =>
        JsonSerializer.Serialize(value, _options);

    public T? Deserialize<T>(RedisValue value) =>
        value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>(value!, _options);

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
                result = JsonSerializer.Deserialize<T>(jsonElement.GetRawText(), _options);
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
