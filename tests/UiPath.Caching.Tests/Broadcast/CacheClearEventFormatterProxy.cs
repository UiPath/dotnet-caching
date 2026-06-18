using System.Text;
using System.Text.Json;

namespace UiPath.Caching.Tests.Broadcast;

public class CacheClearEventFormatterProxy : IEventFormatterProxy<ICacheEvent>
{
    public ICacheEvent? Decode(ReadOnlyMemory<byte> body)
    {
        if(body.IsEmpty)
        {
            return null;
        }

        return JsonSerializer.Deserialize<TestCacheEvent>(body.Span);
    }

    public ReadOnlyMemory<byte> Encode(ICacheEvent clearCacheEvent)
    {
        var str = JsonSerializer.Serialize((TestCacheEvent)clearCacheEvent);
        return Encoding.UTF8.GetBytes(str);
    }
}
