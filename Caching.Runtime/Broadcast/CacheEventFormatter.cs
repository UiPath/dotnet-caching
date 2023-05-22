using System.Text;
using System.Text.Json;

namespace UiPath.Platform.Caching.Broadcast;

public sealed class CacheEventFormatter : IEventFormatterProxy<ICacheEvent>
{
    public ICacheEvent? Decode(ReadOnlyMemory<byte> body) =>
        JsonSerializer.Deserialize<CacheEvent>(body.Span);

    public ReadOnlyMemory<byte> Encode(ICacheEvent clearCacheEvent)
    {
        var str = JsonSerializer.Serialize((CacheEvent)clearCacheEvent);
        return Encoding.UTF8.GetBytes(str);
    }
}
