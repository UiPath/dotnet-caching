using System.Text;
using System.Text.Json;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class CacheClearEventFormatterProxy : IEventFormatterProxy<ICacheEvent>
{
    public ICacheEvent? Decode(ReadOnlyMemory<byte> body) =>
        JsonSerializer.Deserialize<TestClearCacheEvent>(body.Span);

    public ReadOnlyMemory<byte> Encode(ICacheEvent clearCacheEvent)
    {
        var str = JsonSerializer.Serialize((TestClearCacheEvent)clearCacheEvent);
        return Encoding.UTF8.GetBytes(str);
    }
}
