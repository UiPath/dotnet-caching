using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UiPath.Caching.Broadcast;
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

    [Fact]
    public async Task A_verbatim_key_is_read_locally_by_get_and_get_or_add_and_removed_by_its_exact_memory_and_event_key()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var inner = Substitute.For<ICache>();
        inner.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        inner.RemoveAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CancellationToken>()).Returns(true);
        var published = new List<CacheEventData>();
        var sut = Create(memory, inner, published);
        var token = TestContext.Current.CancellationToken;
        var key = CacheKey.Verbatim(Name);
        await sut.SetAsync<string>(key, "v", TimeSpan.FromMinutes(1), policy: null, token);
        var generated = 0;

        var read = await sut.GetAsync<string>(key, policy: null, token);
        var added = await sut.GetOrAddAsync<string>(key, _ => Task.FromResult<string?>("g" + ++generated), policy: null, token);

        read.Should().Be("v");
        added.Should().Be("v");
        generated.Should().Be(0);
        await inner.DidNotReceive().GetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        var before = published.Count;

        (await sut.RemoveAsync<string>(key, token)).Should().BeTrue();

        memory.TryGetValue(Name, out _).Should().BeFalse();
        var removed = inner.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(ICache.RemoveAsync)).Select(c => c.GetArguments().OfType<CacheKey>().Single()).ToList();
        removed.Should().ContainSingle().Which.Should().Match<CacheKey>(k => k.Name == Name && k.Casing == CacheKeyCasing.Verbatim);
        published.Skip(before).Should().NotBeEmpty().And.OnlyContain(d => d.Key == Name);
    }

    [Theory]
    [InlineData("Mixed:Key", "mixed:key")]
    [InlineData("Mixed:Key", " Mixed:Key")]
    [InlineData("Mixed:Key", "Mixed:Key ")]
    public async Task A_serialized_peer_event_invalidates_only_the_entry_whose_key_it_names(string named, string other)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var inner = Substitute.For<ICache>();
        inner.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        using var topic = new FanOutTopic();
        var tokens = new ChangeTokenFactory<byte[]>(Options.Create(new CacheOptions()), new SystemJsonByteSerializerProxy(), NullLoggerFactory.Instance, NullTelemetryProvider.Instance);
        var sut = Create(memory, inner, [], tokens, topic);
        var token = TestContext.Current.CancellationToken;
        await sut.SetAsync<string>(CacheKey.Verbatim(named), "a", TimeSpan.FromMinutes(1), policy: null, token);
        await sut.SetAsync<string>(CacheKey.Verbatim(other), "b", TimeSpan.FromMinutes(1), policy: null, token);
        var formatter = new CacheEventFormatter();
        var wire = formatter.Encode(new CacheEvent { Id = "1", Source = new Uri("urn:peer"), Type = KnownEventTypes.CacheRemoved, Data = new CacheEventData(named) });

        topic.Raise(formatter.Decode(wire)!);

        memory.TryGetValue(named, out _).Should().BeFalse();
        memory.TryGetValue(other, out var kept).Should().BeTrue("an event names exactly one key");
        kept.Should().NotBeNull();
    }

    private static MultilayerCache Create(IMemoryCache memory, ICache inner, List<CacheEventData> published, IChangeTokenFactory? tokens = null, ITopic<ICacheEvent>? topic = null)
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
        if (topic is null)
        {
            var substitute = Substitute.For<ITopic<ICacheEvent>>();
            substitute.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(true);
            topic = substitute;
        }

        var provider = Substitute.For<ITopicProvider>();
        provider.Create(Arg.Any<TopicKey>()).Returns(topic);
        var topics = Substitute.For<ITopicFactory>();
        topics.Get(Arg.Any<string?>()).Returns(provider);
        var options = new InMemoryCacheOptions();
        return new MultilayerCache(
            KnownCacheProviderNames.InMemory,
            inner,
            memories,
            tokens ?? NullChangeTokenFactory.Instance,
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

    private sealed class FanOutTopic : ITopic<ICacheEvent>
    {
        private readonly List<IObserver<ICacheEvent>> _observers = [];

        public TopicKey TopicKey => "t";

        public EventHandler? OnDisposed { get; set; }

        public IDisposable Subscribe(IObserver<ICacheEvent> observer)
        {
            _observers.Add(observer);
            return new Subscription();
        }

        public ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default) => new(true);

        public void Raise(ICacheEvent @event)
        {
            foreach (var observer in _observers.ToArray())
            {
                observer.OnNext(@event);
            }
        }

        public void Dispose()
        {
        }

        private sealed class Subscription : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
