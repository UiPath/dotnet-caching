using System.Text.Json;

namespace UiPath.Caching.Broadcast;

public sealed class CacheEventFormatter : IEventFormatterProxy<ICacheEvent>, IEventSourceMatcherProvider
{
    public ICacheEvent? Decode(ReadOnlyMemory<byte> body) =>
        JsonSerializer.Deserialize<CacheEvent>(body.Span, CacheEventJson.Options);

    public ReadOnlyMemory<byte> Encode(ICacheEvent clearCacheEvent) =>
        JsonSerializer.SerializeToUtf8Bytes((CacheEvent)clearCacheEvent);

    /// <summary>Matches the <c>Source</c> property, wherever it is in the object.</summary>
    public IEventSourceMatcher? CreateSourceMatcher(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return JsonSourceMatcher.Create("Source"u8, source, text => Decode(JsonSerializer.SerializeToUtf8Bytes(new { Source = text })));
    }
}
