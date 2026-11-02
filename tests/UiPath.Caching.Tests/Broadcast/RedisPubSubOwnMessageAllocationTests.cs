namespace UiPath.Caching.Tests.Broadcast;

/// <summary>What a node's own Pub/Sub messages cost its subscriber, per message, over 1000 of them.</summary>
[Collection(AllocationCollection.Name)]
public sealed class RedisPubSubOwnMessageAllocationTests(ITestOutputHelper output)
{
    private const int Messages = 1000;
    private static readonly Uri Self = new("urn:self");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Own_messages_cost_per_message(bool withMatcher)
    {
        IEventFormatterProxy<ICacheEvent> formatter = withMatcher
            ? new RedisPubSubOwnMessageTests.CountingFormatter(new CacheEventFormatter())
            : new RedisPubSubOwnMessageTests.PlainCountingFormatter(new CacheEventFormatter());
        await using var node = await RedisPubSubOwnMessageTests.Node.StartAsync(formatter, Self);
        var payloads = Enumerable.Range(0, Messages).Select(Payload).ToArray();
        node.Receive(payloads[0]);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        foreach (var payload in payloads)
        {
            node.Receive(payload);
        }

        var perMessage = (GC.GetTotalAllocatedBytes(precise: true) - before) / Messages;

        output.WriteLine($"{(withMatcher ? "with" : "without")} the matcher: {perMessage} bytes per own message");
        node.Received.Should().BeEmpty("own messages are not dispatched");
        if (withMatcher)
        {
            perMessage.Should().BeLessThan(16, "an own message is read for its source and dropped (measured {0} bytes)", perMessage);
        }
        else
        {
            perMessage.Should().BeGreaterThan(512, "an own message is decoded to find its source (measured {0} bytes)", perMessage);
        }
    }

    private static byte[] Payload(int i) => new CacheEventFormatter().Encode(new CacheEvent
    {
        Id = Guid.NewGuid().ToString(),
        Type = "UiPath.Caching.Set",
        Source = Self,
        Data = new CacheEventData($"tenant:{i}:user:{i}", new Dictionary<string, object?> { ["cache"] = "users", ["marker"] = "1" }),
    }).ToArray();
}
