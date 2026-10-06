#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>Reads that share one inner read, values kept when the inner tier refuses them, and the local tier across an outage.</summary>
public class InnerTierOutageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A waiter past the lock timeout runs the generator itself, which a loaded machine would otherwise reach.
    private static InMemoryCacheOptions WaitingOutTheGenerator => new() { LocalLockTimeout = TimeSpan.FromSeconds(30) };

    [Fact]
    public async Task Concurrent_cold_reads_of_one_key_share_one_inner_read()
    {
        var inner = Substitute.For<ICache>();
        var release = new TaskCompletionSource<ICacheEntry<string?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { Interlocked.Increment(ref reads); return new ValueTask<ICacheEntry<string?>>(release.Task); });
        using var cache = InMemoryMultilayer.Cache(inner: inner);

        var callers = Enumerable.Range(0, 10).Select(_ => cache.GetAsync<string>("user:42", policy: null, Ct).AsTask()).ToArray();
        release.SetResult(new TestCacheEntry<string?> { Value = "v", Expiration = DateTimeOffset.UtcNow.AddMinutes(5) });

        (await Task.WhenAll(callers)).Should().AllBe("v");
        reads.Should().Be(1);
        (await cache.GetAsync<string>("user:42", policy: null, Ct)).Should().Be("v", "the shared read keeps the hit locally");
        reads.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_cold_reads_with_different_policies_read_separately()
    {
        var inner = Substitute.For<ICache>();
        var release = new TaskCompletionSource<ICacheEntry<string?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { Interlocked.Increment(ref reads); return new ValueTask<ICacheEntry<string?>>(release.Task); });
        using var cache = InMemoryMultilayer.Cache(inner: inner);

        var shortLived = cache.GetAsync<string>("user:42", new CachePolicy { LocalExpiration = TimeSpan.FromSeconds(1) }, Ct).AsTask();
        var longLived = cache.GetAsync<string>("user:42", new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) }, Ct).AsTask();
        release.SetResult(new TestCacheEntry<string?> { Value = "v", Expiration = DateTimeOffset.UtcNow.AddMinutes(5) });

        (await shortLived, await longLived).Should().Be(("v", "v"));
        reads.Should().Be(2, "each keeps the hit with its own policy's local lifetime");
    }

    [Fact]
    public async Task Concurrent_cold_reads_with_equivalent_policies_share_one_inner_read()
    {
        var inner = Substitute.For<ICache>();
        var release = new TaskCompletionSource<ICacheEntry<string?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { Interlocked.Increment(ref reads); return new ValueTask<ICacheEntry<string?>>(release.Task); });
        using var cache = InMemoryMultilayer.Cache(inner: inner);

        var a = cache.GetAsync<string>("user:42", new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) }, Ct).AsTask();
        var b = cache.GetAsync<string>("user:42", new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) }, Ct).AsTask();
        release.SetResult(new TestCacheEntry<string?> { Value = "v", Expiration = DateTimeOffset.UtcNow.AddMinutes(5) });

        (await a, await b).Should().Be(("v", "v"));
        reads.Should().Be(1, "two policies built separately with the same local lifetime read alike");
    }

    [Fact]
    public async Task Concurrent_cold_hash_reads_of_different_fields_share_one_inner_read()
    {
        var inner = Substitute.For<IHashCache>();
        var release = new TaskCompletionSource<ICacheEntry<IDictionary<string, string?>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { Interlocked.Increment(ref reads); return new ValueTask<ICacheEntry<IDictionary<string, string?>>>(release.Task); });
        using var cache = InMemoryMultilayer.HashCache(inner: inner);

        var a = cache.GetItemAsync<string>("user:42", "a", policy: null, Ct).AsTask();
        var b = cache.GetItemAsync<string>("user:42", "b", policy: null, Ct).AsTask();
        release.SetResult(new TestCacheEntry<IDictionary<string, string?>>
        {
            Value = new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2" },
            Expiration = DateTimeOffset.UtcNow.AddMinutes(5),
        });

        (await a, await b).Should().Be(("1", "2"));
        reads.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_misses_run_the_generator_once_when_the_inner_tier_refuses_the_write()
    {
        var inner = Substitute.For<ICache>();
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }));
        inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        using var cache = InMemoryMultilayer.Cache(WaitingOutTheGenerator, inner: inner);
        var generated = 0;
        async Task<string?> Generate(CancellationToken token)
        {
            Interlocked.Increment(ref generated);
            await Task.Delay(50, token);
            return "fresh";
        }

        var callers = Enumerable.Range(0, 8).Select(_ => cache.GetOrAddAsync("user:42", Generate, policy: null, Ct).AsTask()).ToArray();

        (await Task.WhenAll(callers)).Should().AllBe("fresh");
        generated.Should().Be(1, "the callers waiting on the local lock reuse the value the inner tier refused");
    }

    [Fact]
    public async Task Concurrent_hash_misses_run_the_generator_once_when_the_inner_tier_refuses_the_write()
    {
        var inner = Substitute.For<IHashCache>();
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICacheEntry<IDictionary<string, string?>>>(new TestCacheEntry<IDictionary<string, string?>> { Expiration = DateTimeOffset.MinValue }));
        inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        using var cache = InMemoryMultilayer.HashCache(WaitingOutTheGenerator, inner: inner);
        var generated = 0;
        async Task<IDictionary<string, string?>> Generate(CancellationToken token)
        {
            Interlocked.Increment(ref generated);
            await Task.Delay(50, token);
            return new Dictionary<string, string?> { ["f"] = "fresh" };
        }

        var callers = Enumerable.Range(0, 8).Select(_ => cache.GetOrAddAsync<string>("user:42", Generate, policy: null, Ct).AsTask()).ToArray();

        (await Task.WhenAll(callers)).Should().AllSatisfy(values => values.Should().ContainKey("f"));
        generated.Should().Be(1, "the callers waiting on the local lock reuse the value the inner tier refused");
    }

    [Fact]
    public async Task Concurrent_misses_run_the_generator_once_when_the_inner_write_throws()
    {
        var inner = Substitute.For<ICache>();
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }));
        inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<bool>>(_ => throw new InvalidOperationException("inner write failed"));
        using var cache = InMemoryMultilayer.Cache(WaitingOutTheGenerator, inner: inner);
        var generated = 0;
        async Task<string?> Generate(CancellationToken token)
        {
            Interlocked.Increment(ref generated);
            await Task.Delay(50, token);
            return "fresh";
        }

        var callers = Enumerable.Range(0, 8).Select(_ => cache.GetOrAddAsync("user:42", Generate, policy: null, Ct).AsTask()).ToArray();

        (await Task.WhenAll(callers)).Should().AllBe("fresh");
        generated.Should().Be(1, "a write that throws is refused like one that returns false");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_batch_misses_run_the_generator_once_when_the_inner_tier_refuses_the_write(bool throws)
    {
        var inner = BatchMissInner();
        inner.SetAsync(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(_ => throws ? throw new InvalidOperationException("inner write failed") : new ValueTask<bool>(false));
        using var cache = InMemoryMultilayer.Cache(WaitingOutTheGenerator, inner: inner);
        var generated = 0;
        async Task<KeyValuePair<int, string?>[]> Generate(int[] states, CancellationToken token)
        {
            Interlocked.Increment(ref generated);
            await Task.Delay(50, token);
            return Array.ConvertAll(states, s => new KeyValuePair<int, string?>(s, $"fresh{s}"));
        }

        var callers = Enumerable.Range(0, 8)
            .Select(_ => cache.GetOrAddAsync<string, int>([new("user:1", 1), new("user:2", 2)], Generate, policy: null, Ct).AsTask())
            .ToArray();

        (await Task.WhenAll(callers)).Should().AllSatisfy(values => values.Select(v => v.Value).Should().Equal("fresh1", "fresh2"));
        generated.Should().Be(1, "the callers waiting on the local lock reuse the values the inner tier refused");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_batch_miss_canceled_during_the_inner_write_keeps_nothing_local(bool answersFalse)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var inner = BatchMissInner();
        inner.SetAsync(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(_ => CancelWrite(cts, answersFalse), _ => new ValueTask<bool>(false));
        using var cache = InMemoryMultilayer.Cache(inner: inner);
        var generated = 0;
        Task<KeyValuePair<int, string?>[]> Generate(int[] states, CancellationToken _)
        {
            Interlocked.Increment(ref generated);
            return Task.FromResult(Array.ConvertAll(states, s => new KeyValuePair<int, string?>(s, "v")));
        }

        await IgnoreCancellation(() => cache.GetOrAddAsync<string, int>([new("user:1", 1)], Generate, policy: null, cts.Token).AsTask());
        await cache.GetOrAddAsync<string, int>([new("user:1", 1)], Generate, policy: null, Ct);

        generated.Should().Be(2, "the canceled call is not a refusal, so it leaves no local copy behind");
    }

    // A Redis setter answers a write canceled under it with false rather than throwing.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_miss_canceled_during_the_inner_write_keeps_nothing_local(bool hash, bool answersFalse)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var generated = 0;

        if (hash)
        {
            var inner = Substitute.For<IHashCache>();
            inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
                .Returns(new ValueTask<ICacheEntry<IDictionary<string, string?>>>(new TestCacheEntry<IDictionary<string, string?>> { Expiration = DateTimeOffset.MinValue }));
            inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
                .Returns(_ => CancelWrite(cts, answersFalse), _ => new ValueTask<bool>(false));
            using var cache = InMemoryMultilayer.HashCache(inner: inner);
            Task<IDictionary<string, string?>> Generate(CancellationToken _)
            {
                Interlocked.Increment(ref generated);
                return Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = "v" });
            }

            await IgnoreCancellation(() => cache.GetOrAddAsync<string>("user:42", Generate, policy: null, cts.Token).AsTask());
            await cache.GetOrAddAsync<string>("user:42", Generate, policy: null, Ct);
        }
        else
        {
            var inner = Substitute.For<ICache>();
            inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
                .Returns(new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }));
            inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
                .Returns(_ => CancelWrite(cts, answersFalse), _ => new ValueTask<bool>(false));
            using var cache = InMemoryMultilayer.Cache(inner: inner);
            Task<string?> Generate(CancellationToken _)
            {
                Interlocked.Increment(ref generated);
                return Task.FromResult<string?>("v");
            }

            await IgnoreCancellation(() => cache.GetOrAddAsync("user:42", Generate, policy: null, cts.Token).AsTask());
            await cache.GetOrAddAsync("user:42", Generate, policy: null, Ct);
        }

        generated.Should().Be(2, "the canceled call is not a refusal, so it leaves no local copy behind");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_local_write_failure_after_an_accepted_inner_write_is_not_retried(bool hash)
    {
        var sizes = 0;
        var sizeProvider = Substitute.For<ICacheEntrySizeProvider>();
        sizeProvider.GetSize(Arg.Any<ICacheEntry>()).Returns<long>(_ =>
        {
            Interlocked.Increment(ref sizes);
            throw new InvalidOperationException("local write failed");
        });
        var options = new InMemoryCacheOptions { SizeLimit = 1_000, SizeProvider = sizeProvider };
        if (hash)
        {
            var inner = Substitute.For<IHashCache>();
            inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
                .Returns(new ValueTask<ICacheEntry<IDictionary<string, string?>>>(new TestCacheEntry<IDictionary<string, string?>> { Expiration = DateTimeOffset.MinValue }));
            inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
                .Returns(true);
            using var cache = InMemoryMultilayer.HashCache(options, inner);
            await cache.GetOrAddAsync<string>("user:42", _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = "v" }), policy: null, Ct);
        }
        else
        {
            var inner = Substitute.For<ICache>();
            inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
                .Returns(new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }));
            inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
                .Returns(true);
            using var cache = InMemoryMultilayer.Cache(options, inner);
            await cache.GetOrAddAsync("user:42", _ => Task.FromResult<string?>("v"), policy: null, Ct);
        }

        sizes.Should().Be(1, "the inner tier took the write, so the disconnected-cap fallback does not apply");
    }

    [Fact]
    public async Task A_cold_read_during_an_outage_is_kept_for_the_disconnected_cap()
    {
        var inner = Substitute.For<ICache, IConnectionState>();
        var state = (IConnectionState)inner;
        state.IsConnected.Returns(false);
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Value = "v", Expiration = DateTimeOffset.UtcNow.AddHours(1) }));
        var now = DateTimeOffset.UtcNow;
        var systemClock = Substitute.For<Microsoft.Extensions.Internal.ISystemClock>();
        systemClock.UtcNow.Returns(_ => now);
        var clock = new SystemClockTimeProvider(systemClock);
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalMaxExpirationDisconnected = TimeSpan.FromSeconds(30), LocalMaxExpiration = TimeSpan.FromHours(1) }, inner, connectionMonitor: true, clock: clock);
        state.OnConnectionFailed += Raise.Event<EventHandler>(state, EventArgs.Empty);

        (await cache.GetAsync<string>("user:42", policy: null, Ct)).Should().Be("v");
        now = now.AddMinutes(1);
        inner.ClearReceivedCalls();
        await cache.GetAsync<string>("user:42", policy: null, Ct);

        await inner.Received(1).GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Concurrent_cold_reads_with_different_disconnected_caps_read_separately()
    {
        var inner = Substitute.For<ICache>();
        var release = new TaskCompletionSource<ICacheEntry<string?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { Interlocked.Increment(ref reads); return new ValueTask<ICacheEntry<string?>>(release.Task); });
        using var cache = InMemoryMultilayer.Cache(inner: inner);

        var shortCap = cache.GetAsync<string>("user:42", new CachePolicy { LocalExpirationDisconnected = TimeSpan.FromSeconds(1) }, Ct).AsTask();
        var longCap = cache.GetAsync<string>("user:42", new CachePolicy { LocalExpirationDisconnected = TimeSpan.FromMinutes(1) }, Ct).AsTask();
        release.SetResult(new TestCacheEntry<string?> { Value = "v", Expiration = DateTimeOffset.UtcNow.AddMinutes(5) });

        (await shortCap, await longCap).Should().Be(("v", "v"));
        reads.Should().Be(2, "a read that finds the tier down keeps the hit for its own policy's disconnected cap");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cold_batch_read_during_an_outage_is_kept_for_the_disconnected_cap(bool entries)
    {
        var inner = Substitute.For<ICache, IConnectionState>();
        var state = (IConnectionState)inner;
        state.IsConnected.Returns(false);
        inner.GetCacheEntriesAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<KeyValuePair<CacheKey, ICacheEntry<string?>>[]>(call.Arg<CacheKey[]>()
                .Select(k => new KeyValuePair<CacheKey, ICacheEntry<string?>>(k, new TestCacheEntry<string?> { Value = "v", Expiration = DateTimeOffset.UtcNow.AddHours(1) }))
                .ToArray()));
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Value = "v", Expiration = DateTimeOffset.UtcNow.AddHours(1) }));
        var now = DateTimeOffset.UtcNow;
        var systemClock = Substitute.For<Microsoft.Extensions.Internal.ISystemClock>();
        systemClock.UtcNow.Returns(_ => now);
        var clock = new SystemClockTimeProvider(systemClock);
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { LocalMaxExpirationDisconnected = TimeSpan.FromSeconds(30), LocalMaxExpiration = TimeSpan.FromHours(1) }, inner, connectionMonitor: true, clock: clock);
        state.OnConnectionFailed += Raise.Event<EventHandler>(state, EventArgs.Empty);
        CacheKey[] keys = ["user:1", "user:2"];

        if (entries)
        {
            await cache.GetCacheEntriesAsync<string>(keys, policy: null, Ct);
        }
        else
        {
            await cache.GetAsync<string>(keys, policy: null, Ct);
        }

        (await cache.GetAsync<string>("user:1", policy: null, Ct)).Should().Be("v");
        await inner.DidNotReceive().GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        now = now.AddMinutes(1);
        await cache.GetAsync<string>("user:1", policy: null, Ct);

        await inner.Received(1).GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_disconnected_tier_serves_the_local_copy_by_default()
    {
        var inner = Substitute.For<ICache, IConnectionState>();
        var state = (IConnectionState)inner;
        state.IsConnected.Returns(true);
        inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        using var cache = InMemoryMultilayer.Cache(inner: inner, connectionMonitor: true);
        (await cache.SetAsync<string>("user:42", "v", policy: null, Ct)).Should().BeTrue();

        state.IsConnected.Returns(false);
        state.OnConnectionFailed += Raise.Event<EventHandler>(state, EventArgs.Empty);

        (await cache.GetAsync<string>("user:42", policy: null, Ct)).Should().Be("v");
    }

    [Theory]
    [InlineData(null, MissedEventsReason.SubscriptionGap, true)]
    [InlineData(false, MissedEventsReason.SubscriptionGap, false)]
    [InlineData(false, MissedEventsReason.Lost, true)]
    public async Task A_cache_that_keeps_its_local_tier_on_reconnect_keeps_it_over_a_subscription_gap_too(bool? clearLocalOnReconnect, MissedEventsReason reason, bool expired)
    {
        IObserver<ICacheEvent>? observer = null;
        var topic = Substitute.For<ITopic<ICacheEvent>>();
        topic.Subscribe(Arg.Do<IObserver<ICacheEvent>>(o => observer = o)).Returns(Substitute.For<IDisposable>());
        var broadcast = Substitute.For<ITopicProvider, IConnectionState>();
        broadcast.Create(Arg.Any<TopicKey>()).Returns(topic);
        ((IConnectionState)broadcast).IsConnected.Returns(true);
        var tokens = new ChangeTokenFactory<byte[]>(
            Options.Create(new CacheOptions { AppShortName = "test", SourceUri = new Uri("urn:node") }),
            Substitute.For<ISerializerProxy<byte[]>>(),
            NullLoggerFactory.Instance,
            NullTelemetryProvider.Instance);
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { ClearLocalOnReconnect = clearLocalOnReconnect }, ReadOnce(), connectionMonitor: true, topics: Topics(broadcast), tokens: tokens);
        (await cache.GetAsync<string>("user:42", policy: null, Ct)).Should().Be("v");

        ((IMissedEventsObserver)observer!).OnEventsMissed(reason);

        (await cache.GetAsync<string>("user:42", policy: null, Ct)).Should().Be(expired ? null : "v");
    }

    [Theory]
    [InlineData(null, MissedEventsReason.SubscriptionGap, true)]
    [InlineData(false, MissedEventsReason.SubscriptionGap, false)]
    [InlineData(false, MissedEventsReason.Lost, true)]
    public async Task A_custom_token_factory_keeps_entries_over_a_subscription_gap_when_the_cache_does(bool? clearLocalOnReconnect, MissedEventsReason reason, bool expired)
    {
        IObserver<ICacheEvent>? observer = null;
        var topic = Substitute.For<ITopic<ICacheEvent>>();
        topic.Subscribe(Arg.Do<IObserver<ICacheEvent>>(o => observer = o)).Returns(Substitute.For<IDisposable>());
        var broadcast = Substitute.For<ITopicProvider, IConnectionState>();
        broadcast.Create(Arg.Any<TopicKey>()).Returns(topic);
        ((IConnectionState)broadcast).IsConnected.Returns(true);
        var tokens = new TestChangeTokenFactory((key, t) => new ChangeToken<byte[]>(
            key,
            t,
            new Uri("urn:node"),
            Substitute.For<ISerializerProxy<byte[]>>(),
            NullLogger<ChangeToken<byte[]>>.Instance,
            NullTelemetryProvider.Instance));
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { ClearLocalOnReconnect = clearLocalOnReconnect }, ReadOnce(), connectionMonitor: true, topics: Topics(broadcast), tokens: tokens);
        (await cache.GetAsync<string>("user:42", policy: null, Ct)).Should().Be("v");

        ((IMissedEventsObserver)observer!).OnEventsMissed(reason);

        (await cache.GetAsync<string>("user:42", policy: null, Ct)).Should().Be(expired ? null : "v");
    }

    [Fact]
    public async Task A_restored_connection_keeps_the_local_tier_when_no_broadcast_could_be_missed()
    {
        var inner = Substitute.For<ICache, IConnectionState>();
        var state = (IConnectionState)inner;
        state.IsConnected.Returns(true);
        StubReadOnce(inner);
        using var cache = InMemoryMultilayer.Cache(inner: inner, connectionMonitor: true);
        (await cache.GetAsync<string>("user:42", policy: null, Ct)).Should().Be("v");

        state.IsConnected.Returns(false);
        state.OnConnectionFailed += Raise.Event<EventHandler>(state, EventArgs.Empty);
        state.IsConnected.Returns(true);
        state.OnConnectionRestored += Raise.Event<EventHandler>(state, EventArgs.Empty);

        (await cache.GetAsync<string>("user:42", policy: null, Ct)).Should().Be("v");
    }

    [Fact]
    public async Task A_restored_connection_keeps_the_local_tier_when_broadcast_runs_over_streams()
    {
        var connector = Substitute.For<IRedisConnector>();
        connector.IsConnected.Returns(true);
        using var streams = StreamsProvider(connector);
        using var cache = InMemoryMultilayer.Cache(inner: ReadOnce(), connectionMonitor: true, topics: Topics(streams));
        (await cache.GetAsync<string>("user:42", policy: null, Ct)).Should().Be("v");

        connector.IsConnected.Returns(false);
        connector.OnConnectionFailed += Raise.Event<EventHandler>(connector, EventArgs.Empty);
        connector.IsConnected.Returns(true);
        connector.OnConnectionRestored += Raise.Event<EventHandler>(connector, EventArgs.Empty);

        (await cache.GetAsync<string>("user:42", policy: null, Ct)).Should().Be("v", "the stream replays what the outage held back");
    }

    [Fact]
    public void The_connection_monitor_is_on_by_default() =>
        new CacheOptions().ConnectionMonitorEnabled.Should().BeTrue();

    private static async Task IgnoreCancellation(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>An inner tier that holds the value for the first read only, so a later hit can only come from the local tier.</summary>
    private static ICache ReadOnce()
    {
        var inner = Substitute.For<ICache>();
        StubReadOnce(inner);
        return inner;
    }

    private static void StubReadOnce(ICache inner) =>
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(
            new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Value = "v", Expiration = DateTimeOffset.UtcNow.AddMinutes(5) }),
            new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }));

    private static ITopicFactory Topics(ITopicProvider provider)
    {
        var topics = Substitute.For<ITopicFactory>();
        topics.Get(Arg.Any<string?>()).Returns(provider);
        return topics;
    }

    private static RedisStreamsTopicProvider StreamsProvider(IRedisConnector connector) => new(
        Options.Create(new RedisStreamsTopicOptions { Enabled = true, ConnectionMonitorEnabled = true }),
        Options.Create(new CacheOptions { AppShortName = "test" }),
        new PerTopicOptionsRegistry<RedisStreamsTopicOptions>(new ConfigurationBuilder().Build().GetSection("Topics")),
        connector,
        Substitute.For<IEventFormatterProxy<ICacheEvent>>(),
        Substitute.For<IResiliencePipelineProvider>(),
        NullLoggerFactory.Instance,
        NullTelemetryProvider.Instance,
        NullRedisProfiler.Instance);

    private static ValueTask<bool> CancelWrite(CancellationTokenSource cts, bool answersFalse)
    {
        cts.Cancel();
        return answersFalse ? new ValueTask<bool>(false) : throw new OperationCanceledException(cts.Token);
    }

    private static ICache BatchMissInner()
    {
        var inner = Substitute.For<ICache>();
        inner.GetCacheEntriesAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<KeyValuePair<CacheKey, ICacheEntry<string?>>[]>(Array.ConvertAll(
                ci.Arg<CacheKey[]>(),
                k => new KeyValuePair<CacheKey, ICacheEntry<string?>>(k, new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }))));
        return inner;
    }
}
