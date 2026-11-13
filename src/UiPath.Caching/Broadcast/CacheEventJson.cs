using System.Text.Json;

namespace UiPath.Caching.Broadcast;

internal static class CacheEventJson
{
    public static JsonSerializerOptions Options { get; } = new() { Converters = { new CacheEventPropertiesConverter() } };
}
