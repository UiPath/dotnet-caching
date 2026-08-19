using Microsoft.Extensions.Caching.Memory;
using UiPath.Caching.Locking;

namespace UiPath.Caching.Tests;

public class MultilayerCacheBatchGetOrAddTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private static readonly long[] States1 = [1L];
    private static readonly long[] States7 = [7L];
    private static readonly long[] States1And2 = [1L, 2L];
    private static readonly long[] States2And3 = [2L, 3L];
    private static readonly long[] States2Then1 = [2L, 1L];
    private static readonly long[] States1To3 = [1L, 2L, 3L];
    private static readonly string?[] AAndB = ["A", "B"];
    private static readonly string?[] AGen2Gen3 = ["A", "gen:2", "gen:3"];
    private static readonly string?[] Gen1AndGen2 = ["gen:1", "gen:2"];
    private static readonly string?[] Gen1Twice = ["gen:1", "gen:1"];
    private static readonly int[] SeededOfTen = [2, 5, 9];
    private static readonly long[] MissingOfTen = [1L, 3L, 4L, 6L, 7L, 8L, 10L];

    private ICache _innerCache = default!;
    private MemoryCache _memoryCache = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private ITopicKeyStrategy _topicKeyStrategy = default!;
    private ITopicFactory _topicFactory = default!;
    private MultilayerCacheTests.ITopicProviderWithConnectionState _topicProvider = default!;
    private ITopic<ICacheEvent> _topic = default!;
    private IChangeTokenFactory _changeTokenFactory = default!;
    private IMemoryCacheFactory _memoryCacheFactory = default!;
    private ICacheEventFactory _cacheEventFactory = default!;
    private AsyncKeyedLocalLock _locker = default!;
    private InMemoryRedisCacheOptions _options = default!;
    private TopicKey _topicKey = default!;
    private MultilayerCache? _sut;

    private MultilayerCache Sut => _sut ??= _fixture.Create<MultilayerCache>();

    private readonly Dictionary<CacheKey, string?> _stored = [];
    private readonly List<long[]> _generatorCalls = [];
    private readonly List<CacheKey[]> _innerSetCalls = [];

    private Task<KeyValuePair<long, string?>[]> Generate(long[] states, CancellationToken _)
    {
        _generatorCalls.Add(states);
        return Task.FromResult(states.Select(s => new KeyValuePair<long, string?>(s, "gen:" + s)).ToArray());
    }

    [Fact]
    public async Task Generator_receives_only_missing_states_and_runs_once()
    {
        _stored[(CacheKey)"a"] = "A";

        var result = await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L), new((CacheKey)"b", 2L), new((CacheKey)"c", 3L)],
            Generate,
            (CachePolicy?)null,
            testContextAccessor.Current.CancellationToken);

        _generatorCalls.Should().HaveCount(1);
        _generatorCalls[0].Should().Equal(States2And3, "only the states whose keys missed are requested");
        result.Select(r => r.Key).Should().Equal(States1To3, "results come back keyed by state, in request order");
        result.Select(r => r.Value).Should().Equal(AGen2Gen3);
    }

    [Fact]
    public async Task Large_batch_calls_the_generator_exactly_once_with_every_missing_state()
    {
        var entries = Enumerable.Range(1, 10)
            .Select(i => new KeyValuePair<CacheKey, long>((CacheKey)("k:" + i), (long)i))
            .ToArray();
        foreach (var i in SeededOfTen)
        {
            _stored[(CacheKey)("k:" + i)] = "hit:" + i;
        }

        var result = await Sut.GetOrAddAsync<string, long>(
            entries, Generate, (CachePolicy?)null, testContextAccessor.Current.CancellationToken);

        _generatorCalls.Should().HaveCount(1, "the multi-key generator runs once per call, never once per key");
        _generatorCalls[0].Should().Equal(
            MissingOfTen,
            "every missing state arrives in a single array, in request order");
        _innerSetCalls.Should().HaveCount(1, "the seven generated values are written in one batch");
        _innerSetCalls[0].Should().HaveCount(7);
        result.Should().HaveCount(10);
        result.Single(r => r.Key == 5L).Value.Should().Be("hit:5");
        result.Single(r => r.Key == 6L).Value.Should().Be("gen:6");
    }

    [Fact]
    public async Task Caller_keys_that_map_to_one_cache_key_collapse_to_a_single_entry()
    {
        _cacheKeyStrategy.GetCacheKey<string>(Arg.Any<CacheKey>()).Returns((CacheKey)"shared");

        var result = await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)],
            Generate,
            (CachePolicy?)null,
            testContextAccessor.Current.CancellationToken);

        _generatorCalls.Should().HaveCount(1);
        _generatorCalls[0].Should().Equal(States1, "the two caller keys are one cache entry, so the generator is asked once");
        _innerSetCalls.Should().HaveCount(1);
        _innerSetCalls[0].Should().Equal(new[] { (CacheKey)"shared" }, "the mapped key is written once, not once per caller key");
        result.Select(r => r.Key).Should().Equal(States1And2, "one result per distinct state");
        result.Select(r => r.Value).Should().Equal(Gen1Twice, "both states carry the one generated value");
    }

    [Fact]
    public async Task Missing_keys_are_written_to_the_inner_cache_in_one_batch()
    {
        await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)],
            Generate,
            (CachePolicy?)null,
            testContextAccessor.Current.CancellationToken);

        _innerSetCalls.Should().HaveCount(1, "one batch write, not one per key");
        _innerSetCalls[0].Should().BeEquivalentTo(new[] { (CacheKey)"a", (CacheKey)"b" });
    }

    [Fact]
    public async Task Second_call_is_served_from_local_memory_without_touching_the_inner_cache()
    {
        var token = testContextAccessor.Current.CancellationToken;
        await Sut.GetOrAddAsync<string, long>([new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)], Generate, (CachePolicy?)null, token);
        _innerCache.ClearReceivedCalls();

        var result = await Sut.GetOrAddAsync<string, long>([new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)], Generate, (CachePolicy?)null, token);

        result.Select(r => r.Value).Should().Equal(Gen1AndGen2);
        _generatorCalls.Should().HaveCount(1, "L1 must serve the second call");
        await _innerCache.DidNotReceiveWithAnyArgs().GetCacheEntriesAsync<string>(default!, default, token);
    }

    [Fact]
    public async Task State_omitted_by_the_generator_is_not_cached()
    {
        var token = testContextAccessor.Current.CancellationToken;

        var result = await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)],
            (states, _) =>
            {
                _generatorCalls.Add(states);
                return Task.FromResult<KeyValuePair<long, string?>[]>([new(1L, "A")]);
            },
            (CachePolicy?)null,
            token);

        result.Single(r => r.Key == 2L).Value.Should().BeNull();
        _stored.Should().NotContainKey((CacheKey)"b");
        _innerSetCalls.Single().Should().Equal(new[] { (CacheKey)"a" });
    }

    [Fact]
    public async Task Explicit_null_is_not_written_when_CacheNullValues_is_off()
    {
        _options.CacheNullValues = false;
        _sut = null;
        var token = testContextAccessor.Current.CancellationToken;

        var result = await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L)],
            (states, _) => { _generatorCalls.Add(states); return Task.FromResult<KeyValuePair<long, string?>[]>([new(1L, null)]); },
            (CachePolicy?)null,
            token);

        result.Single().Value.Should().BeNull();
        _innerSetCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_null_is_written_when_CacheNullValues_is_on()
    {
        _options.CacheNullValues = true;
        _sut = null;
        var token = testContextAccessor.Current.CancellationToken;

        await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L)],
            (states, _) => { _generatorCalls.Add(states); return Task.FromResult<KeyValuePair<long, string?>[]>([new(1L, null)]); },
            (CachePolicy?)null,
            token);

        _innerSetCalls.Single().Should().Equal(new[] { (CacheKey)"a" });
    }

    [Fact]
    public async Task Duplicate_request_entries_collapse_and_order_is_preserved()
    {
        var result = await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"b", 2L), new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)],
            Generate,
            (CachePolicy?)null,
            testContextAccessor.Current.CancellationToken);

        result.Select(r => r.Key).Should().Equal(States2Then1);
        _generatorCalls.Single().Should().Equal(States2Then1);
    }

    [Fact]
    public async Task Two_states_sharing_one_key_are_generated_once_and_both_get_the_value()
    {
        var result = await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"shared", 1L), new((CacheKey)"shared", 2L)],
            Generate,
            (CachePolicy?)null,
            testContextAccessor.Current.CancellationToken);

        _generatorCalls.Single().Should().Equal(States1, "one request per distinct key, carrying the first state seen for it");
        result.Select(r => r.Key).Should().Equal(States1And2, "one result per distinct state");
        result.Select(r => r.Value).Should().Equal(Gen1Twice, "the one generated value fans out onto both states");
        _innerSetCalls.Single().Should().Equal(new[] { (CacheKey)"shared" }, "one write per distinct key");
    }

    [Fact]
    public async Task All_hits_never_invoke_the_generator()
    {
        _stored[(CacheKey)"a"] = "A";
        _stored[(CacheKey)"b"] = "B";

        var result = await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)],
            Generate,
            (CachePolicy?)null,
            testContextAccessor.Current.CancellationToken);

        _generatorCalls.Should().BeEmpty();
        result.Select(r => r.Value).Should().Equal(AAndB);
    }

    [Fact]
    public async Task Empty_request_short_circuits()
    {
        var token = testContextAccessor.Current.CancellationToken;

        var result = await Sut.GetOrAddAsync<string, long>([], Generate, (CachePolicy?)null, token);

        result.Should().BeEmpty();
        _generatorCalls.Should().BeEmpty();
        await _innerCache.DidNotReceiveWithAnyArgs().GetCacheEntriesAsync<string>(default!, default, token);
    }

    [Fact]
    public async Task Generator_receives_states_while_the_inner_cache_sees_mapped_keys()
    {
        _cacheKeyStrategy.GetCacheKey<string>(Arg.Any<CacheKey>())
            .Returns(c => (CacheKey)("p:" + c.Arg<CacheKey>().Name));
        _sut = null;

        var result = await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 7L)],
            (ids, _) => { _generatorCalls.Add(ids); return Task.FromResult(ids.Select(id => new KeyValuePair<long, string?>(id, "gen:" + id)).ToArray()); },
            (CachePolicy?)null,
            testContextAccessor.Current.CancellationToken);

        _generatorCalls.Single().Should().Equal(States7, "the generator sees state, never a key");
        result.Single().Key.Should().Be(7L);
        _innerSetCalls.Single().Should().Equal(new[] { (CacheKey)"p:a" }, "the inner cache is written with mapped keys");
    }

    [Fact]
    public async Task Expiration_overloads_flow_through_to_the_inner_write()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var expiration = DateTimeOffset.UtcNow.AddMinutes(7);

        await Sut.GetOrAddAsync<string, long>([new((CacheKey)"a", 1L)], Generate, expiration, null, token);
        await Sut.GetOrAddAsync<string, long>([new((CacheKey)"b", 2L)], Generate, TimeSpan.FromMinutes(3), null, token);

        _innerSetCalls.Should().HaveCount(2);
        await _innerCache.Received(1).SetAsync<string?>(
            Arg.Any<KeyValuePair<CacheKey, string?>[]>(),
            Arg.Is<DateTimeOffset?>(d => d.HasValue && d.Value == expiration),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Non_cacheable_type_throws_before_any_io()
    {
        var token = testContextAccessor.Current.CancellationToken;

        await Assert.ThrowsAsync<NotCacheableException>(async () =>
            await Sut.GetOrAddAsync<int, long>(
                [new((CacheKey)"a", 1L)],
                (_, _) => Task.FromResult<KeyValuePair<long, int>[]>([]),
                (CachePolicy?)null,
                token));

        await _innerCache.DidNotReceiveWithAnyArgs().GetCacheEntriesAsync<int>(default!, default, token);
    }

    public ValueTask InitializeAsync()
    {
        _topicKey = _fixture.Create<string>();
        _changeTokenFactory = _fixture.Freeze<IChangeTokenFactory>();
        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(_ => new TestChangeToken { ActiveChangeCallbacks = true, HasChanged = false });
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        _innerCache = _fixture.Freeze<ICache>();
        _options = new InMemoryRedisCacheOptions
        {
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EntryFactory = new TestCacheEntryFactory(),
            CacheNullValues = true,
        };
        _locker = new AsyncKeyedLocalLock(Options.Create(new CacheOptions()));

        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        _cacheKeyStrategy.GetCacheKey<string>(Arg.Any<CacheKey>()).Returns(c => c.Arg<CacheKey>());
        _options.CacheKeyStrategy = _cacheKeyStrategy;
        _topicKeyStrategy = _fixture.Create<ITopicKeyStrategy>();
        _topicKeyStrategy.GetTopicKey<string>().Returns(_topicKey);
        _options.TopicKeyStrategy = _topicKeyStrategy;
        _topicFactory = _fixture.Freeze<ITopicFactory>();
        _topicProvider = _fixture.Freeze<MultilayerCacheTests.ITopicProviderWithConnectionState>();
        _topic = _fixture.Freeze<ITopic<ICacheEvent>>();
        _topicFactory.Get(Arg.Any<string>()).Returns(_topicProvider);
        _topicProvider.Create(_topicKey).Returns(_topic);
        _topicProvider.IsConnected.Returns(true);
        _memoryCacheFactory = _fixture.Freeze<IMemoryCacheFactory>();
        _memoryCacheFactory.Get(Arg.Any<IMemoryCacheOptions>()).Returns(_ => _memoryCache);
        _fixture.Inject<IMultilayerCacheOptions>(_options);
        _fixture.Inject<IMemoryCacheOptions>(_options);
        _fixture.Inject<ILocalLock>(_locker);
        _cacheEventFactory = _fixture.Freeze<ICacheEventFactory>();
        _cacheEventFactory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
            .Returns(c => new Broadcast.TestCacheEvent
            {
                Id = c.ArgAt<string?>(3),
                Data = c.Arg<CacheEventData>(),
                Type = c.ArgAt<string>(1),
            });

        _innerCache.GetCacheEntriesAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<CacheKey[]>()!
                .Select(k => new KeyValuePair<CacheKey, ICacheEntry<string?>>(
                    k,
                    _stored.TryGetValue(k, out var v)
                        ? new TestCacheEntry<string?> { Value = v, Expiration = DateTimeOffset.UtcNow.AddMinutes(10), Found = true }
                        : new TestCacheEntry<string?> { Value = null, Expiration = DateTimeOffset.MinValue }))
                .ToArray());

        _innerCache.SetAsync<string?>(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                var pairs = c.Arg<KeyValuePair<CacheKey, string?>[]>()!;
                _innerSetCalls.Add(pairs.Select(p => p.Key).ToArray());
                foreach (var pair in pairs) { _stored[pair.Key] = pair.Value; }
                return true;
            });

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _locker?.Dispose();
        _memoryCache?.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
