using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests;

public class VerbatimKeyEndToEndTests
{
    private const string Name = " Mixed:Key ";

    [Fact]
    public async Task A_verbatim_key_reaches_the_inner_tier_the_memory_entry_and_the_event_unchanged()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var inner = Substitute.For<ICache>();
        inner.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        var published = new List<CacheEventData>();
        var sut = Create(memory, inner, published);

        await sut.SetAsync<string>(CacheKey.Verbatim(Name), "v", TimeSpan.FromMinutes(1), policy: null, TestContext.Current.CancellationToken);

        var innerKeys = inner.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICache.SetAsync))
            .Select(c => c.GetArguments().OfType<CacheKey>().Single())
            .ToList();
        innerKeys.Should().NotBeEmpty().And.OnlyContain(k => k.Name == Name && k.Casing == CacheKeyCasing.Verbatim);
        memory.TryGetValue(Name, out _).Should().BeTrue();
        memory.TryGetValue(Name.Trim().ToLowerInvariant(), out _).Should().BeFalse();
        published.Should().NotBeEmpty().And.OnlyContain(d => d.Key == Name);
    }

    private static MultilayerCache Create(IMemoryCache memory, ICache inner, List<CacheEventData> published)
    {
        var memories = Substitute.For<IMemoryCacheFactory>();
        memories.Get(Arg.Any<IMemoryCacheOptions>()).Returns(memory);
        var events = Substitute.For<ICacheEventFactory>();
        events.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
            .Returns(call =>
            {
                published.Add(call.Arg<CacheEventData>());
                return new CacheEvent { Id = "1", Source = new Uri("urn:test"), Type = call.ArgAt<string>(1), Data = call.Arg<CacheEventData>() };
            });
        var topic = Substitute.For<ITopic<ICacheEvent>>();
        topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(true);
        var provider = Substitute.For<ITopicProvider>();
        provider.Create(Arg.Any<TopicKey>()).Returns(topic);
        var topics = Substitute.For<ITopicFactory>();
        topics.Get(Arg.Any<string?>()).Returns(provider);
        var options = new InMemoryCacheOptions();
        return new MultilayerCache(
            KnownCacheProviderNames.InMemory,
            inner,
            memories,
            NullChangeTokenFactory.Instance,
            topics,
            events,
            NullTelemetryProvider.Instance,
            options,
            options,
            new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = false },
            distributedLock: NullDistributedLock.Instance,
            policyFactory: NullCachePolicyFactory.Instance,
            clock: TimeProvider.System,
            logger: NullLogger.Instance);
    }
}
