using Microsoft.Extensions.Logging.Abstractions;

namespace UiPath.Caching.Tests;

public class CacheEventPublisherTests
{
    public static TheoryData<string, string> Raises => new()
    {
        { nameof(CacheEventPublisher.MetadataUpdatedAsync), KnownEventTypes.CacheRefreshed },
        { nameof(CacheEventPublisher.CacheSetAsync), KnownEventTypes.CacheSet },
        { nameof(CacheEventPublisher.CacheRefreshedAsync), KnownEventTypes.CacheRefreshed },
        { nameof(CacheEventPublisher.CacheRemovedAsync), KnownEventTypes.CacheRemoved },
    };

    [Theory]
    [MemberData(nameof(Raises))]
    public async Task Each_notification_raises_its_own_event_type(string method, string eventType)
    {
        var factory = Substitute.For<ICacheEventFactory>();
        var topics = Substitute.For<ITopicProvider>();
        topics.Create(Arg.Any<TopicKey>()).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(true);
        var options = Substitute.For<ICacheEntryOptions>();
        options.CacheKey.Returns(new CacheKey("user:42"));
        var sut = new CacheEventPublisher("cache", topics, factory, NullLogger.Instance);

        var raised = method switch
        {
            nameof(CacheEventPublisher.MetadataUpdatedAsync) => await sut.MetadataUpdatedAsync(options),
            nameof(CacheEventPublisher.CacheSetAsync) => await sut.CacheSetAsync(options),
            nameof(CacheEventPublisher.CacheRefreshedAsync) => await sut.CacheRefreshedAsync(options),
            _ => await sut.CacheRemovedAsync(options),
        };

        raised.Should().BeTrue();
        factory.Received(1).Create("cache", eventType, Arg.Any<CacheEventData>());
    }

    [Fact]
    public async Task Default_event_bytes_are_unchanged()
    {
        var sets = await Encoded(sut => sut.CacheSetAsync(Options(), typeof(string)));
        var peerUpdate = await Encoded(sut => sut.CacheSetAsync(Options(), typeof(string), 7, new Dictionary<string, byte[]?> { ["a"] = [1, 2], ["b"] = null }));

        Utf8(sets).Should().Be("""{"Id":"1","Source":"urn:test","Data":{"Key":"user:42","Properties":null},"Type":"set","TransportId":null,"Key":"user:42"}""");
        Utf8(peerUpdate).Should().Be("""{"Id":"1","Source":"urn:test","Data":{"Key":"user:42","Properties":{"_version_":7,"_peerFields_":{"a":"AQI=","b":null},"_peerExpiration_":"2030-01-02T03:04:05+00:00","_peerMetadata_":{"m":"v"}}},"Type":"set","TransportId":null,"Key":"user:42"}""");
    }

    private static CacheEntryOptions Options() => new()
    {
        CacheKey = new CacheKey("user:42"),
        TopicKey = "t",
        Expiration = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero),
        Metadata = new Dictionary<string, string?> { ["m"] = "v" },
    };

    private static string Utf8(ReadOnlyMemory<byte> bytes) => System.Text.Encoding.UTF8.GetString(bytes.Span);

    private static async Task<ReadOnlyMemory<byte>> Encoded(Func<CacheEventPublisher, ValueTask<bool>> raise)
    {
        CacheEventData? published = null;
        var factory = Substitute.For<ICacheEventFactory>();
        factory.Create("cache", Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
            .Returns(call =>
            {
                published = call.Arg<CacheEventData>();
                return new CacheEvent { Id = "1", Source = new Uri("urn:test"), Type = call.ArgAt<string>(1), Data = published };
            });
        var topics = Substitute.For<ITopicProvider>();
        topics.Create(Arg.Any<TopicKey>()).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(true);

        (await raise(new CacheEventPublisher("cache", topics, factory, NullLogger.Instance))).Should().BeTrue();

        return new CacheEventFormatter().Encode(new CacheEvent { Id = "1", Source = new Uri("urn:test"), Type = "set", Data = published });
    }
}
