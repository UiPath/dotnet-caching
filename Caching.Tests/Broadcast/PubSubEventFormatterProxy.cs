using System.Text;
using System.Text.Json;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class PubSubEventFormatterProxy : IEventFormatterProxy<IPubSubEvent>
{
    public IPubSubEvent? Decode(ReadOnlyMemory<byte> body) =>
        JsonSerializer.Deserialize<TestPubSubEvent>(body.Span);

    public ReadOnlyMemory<byte> Encode(IPubSubEvent clearCacheEvent)
    {
        var str = JsonSerializer.Serialize((TestPubSubEvent)clearCacheEvent);
        return Encoding.UTF8.GetBytes(str);
    }
}
