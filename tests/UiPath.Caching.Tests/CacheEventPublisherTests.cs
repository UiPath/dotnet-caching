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
}
