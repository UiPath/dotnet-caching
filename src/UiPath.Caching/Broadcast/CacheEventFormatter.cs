using System.Text.Json;

namespace UiPath.Caching.Broadcast;

public sealed class CacheEventFormatter : IEventFormatterProxy<ICacheEvent>
{
    public ICacheEvent? Decode(ReadOnlyMemory<byte> body) =>
        JsonSerializer.Deserialize<CacheEvent>(body.Span);

    public ReadOnlyMemory<byte> Encode(ICacheEvent clearCacheEvent) =>
        JsonSerializer.SerializeToUtf8Bytes((CacheEvent)clearCacheEvent);
}
