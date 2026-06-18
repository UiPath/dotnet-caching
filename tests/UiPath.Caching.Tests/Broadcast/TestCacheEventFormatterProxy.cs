using System.Text;
using System.Text.Json;

namespace UiPath.Caching.Tests.Broadcast;

public class TestCacheEventFormatterProxy : IEventFormatterProxy<ICacheEvent>
{
    public ICacheEvent? Decode(ReadOnlyMemory<byte> body) =>
        JsonSerializer.Deserialize<TestCacheEvent>(body.Span);

    public ReadOnlyMemory<byte> Encode(ICacheEvent cacheEvent)
    {
        var str = JsonSerializer.Serialize((TestCacheEvent)cacheEvent);
        return Encoding.UTF8.GetBytes(str);
    }
}
