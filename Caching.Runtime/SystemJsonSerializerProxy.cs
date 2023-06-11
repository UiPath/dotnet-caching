using System.Text.Json;

namespace UiPath.Platform.Caching;

public class SystemJsonSerializerProxy : ISerializerProxy
{
    private readonly JsonSerializerOptions? _options;

    public SystemJsonSerializerProxy(JsonSerializerOptions? options = null) =>
        _options = options;

    public string? Serialize(object? value) =>
        JsonSerializer.Serialize(value, _options);

    public T? Deserialize<T>(string? value) =>
        string.IsNullOrWhiteSpace(value) ? default : JsonSerializer.Deserialize<T>(value, _options);
}
