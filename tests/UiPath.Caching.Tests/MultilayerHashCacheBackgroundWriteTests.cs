#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>The opt-in background write on the hash cache, with the guarantees of the single-key cache.</summary>
public class MultilayerHashCacheBackgroundWriteTests
{
    private static readonly CachePolicy Awaited = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30) };

    private static readonly CachePolicy Background = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30), BackgroundDistributedWrite = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_background_set_returns_with_the_hash_local_and_the_write_issued_but_not_finished()
    {
        using var h = new Rig();
        var gate = h.Gate("k");

        (await h.Cache.SetAsync<string>("k", Fields("v"), Background, Ct)).Should().BeTrue();

        h.Log.Should().Equal("write:k=v");
        h.Local("k").Should().Be("v");
        gate.SetResult(true);
        await h.SettledAsync();
        h.Log.Should().Equal("write:k=v", "event:k");
        h.WrittenWhenPublished.Should().Equal(true);
    }

    [Fact]
    public async Task A_background_set_with_options_returns_before_its_write_finishes()
    {
        using var h = new Rig();
        var gate = h.Gate("k");

        (await h.Cache.SetAsync<string>("k", Fields("v"), new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5)), Background, Ct)).Should().BeTrue();

        h.Log.Should().Equal("write:k=v");
        gate.SetResult(true);
        await h.SettledAsync();
        h.Log.Should().Equal("write:k=v", "event:k");
    }

    [Fact]
    public async Task A_background_set_is_promoted_to_the_normal_lifetime_once_the_write_lands()
    {
        using var h = new Rig();

        await h.Cache.SetAsync<string>("k", Fields("v"), Background, Ct);
        await h.SettledAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        h.Local("k").Should().Be("v");
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        h.Local("k").Should().BeNull();
    }

    [Fact]
    public async Task A_background_set_the_inner_tier_refuses_stays_local_for_the_cap_only_and_sends_no_event()
    {
        using var h = new Rig { WriteResult = false };

        (await h.Cache.SetAsync<string>("k", Fields("v"), Background, Ct)).Should().BeTrue();
        await h.SettledAsync();

        h.Log.Should().Equal("write:k=v");
        h.Clock.Advance(TimeSpan.FromSeconds(29));
        h.Local("k").Should().Be("v");
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Local("k").Should().BeNull();
        h.Telemetry.Received(1).TrackEvent("cache.background_write.failed", Arg.Any<TelemetryTags<string>>(), Arg.Any<TelemetryTags<double>>());
    }

    [Fact]
    public async Task A_background_set_whose_write_throws_is_counted_and_stays_local_for_the_cap_only()
    {
        using var h = new Rig { WriteThrows = true };

        (await h.Cache.SetAsync<string>("k", Fields("v"), Background, Ct)).Should().BeTrue();
        await h.SettledAsync();

        h.Telemetry.Received(1).TrackEvent("cache.background_write.failed", Arg.Any<TelemetryTags<string>>(), Arg.Any<TelemetryTags<double>>());
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        h.Local("k").Should().BeNull();
    }

    [Fact]
    public async Task A_background_set_whose_event_is_refused_is_counted_and_still_promoted()
    {
        using var h = new Rig { PublishResult = false };

        (await h.Cache.SetAsync<string>("k", Fields("v"), Background, Ct)).Should().BeTrue();
        await h.SettledAsync();

        h.Telemetry.Received(1).TrackEvent("cache.background_write.failed", Arg.Any<TelemetryTags<string>>(), Arg.Any<TelemetryTags<double>>());
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Local("k").Should().Be("v");
    }

    [Fact]
    public async Task A_promotion_does_not_overwrite_a_newer_local_value()
    {
        using var h = new Rig();
        var first = h.Gate("k", "a");
        h.Gate("k", "b");

        await h.Cache.SetAsync<string>("k", Fields("a"), Background, Ct);
        await h.Cache.SetAsync<string>("k", Fields("b"), Background, Ct);
        first.SetResult(true);
        await h.UntilAsync(() => h.Log.Contains("event:k"));

        h.Local("k").Should().Be("b");
        h.ReleaseAll();
        await h.SettledAsync();
        h.Local("k").Should().Be("b");
    }

    [Fact]
    public async Task A_remove_after_a_background_set_waits_for_it_and_leaves_nothing_local()
    {
        using var h = new Rig();
        var gate = h.Gate("k");
        await h.Cache.SetAsync<string>("k", Fields("v"), Background, Ct);

        var remove = h.Cache.RemoveAsync<string>("k", Ct).AsTask();

        await Task.Delay(100, Ct);
        remove.IsCompleted.Should().BeFalse("the unlink must not overtake the write it follows");
        gate.SetResult(true);
        (await remove).Should().BeTrue();
        await h.SettledAsync();
        h.Log.Should().Equal("write:k=v", "event:k", "remove:k", "event:k");
        h.Local("k").Should().BeNull();
    }

    [Fact]
    public async Task A_refresh_and_a_metadata_write_after_a_background_set_wait_for_it()
    {
        using var h = new Rig();
        var gate = h.Gate("k");
        await h.Cache.SetAsync<string>("k", Fields("v"), Background, Ct);

        var refresh = h.Cache.RefreshAsync<string>("k", new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(5)), Background, Ct).AsTask();
        var metadata = h.Cache.SetMetadataAsync<string>("k", new Dictionary<string, string?> { ["m"] = "1" }, Ct).AsTask();

        await Task.Delay(100, Ct);
        refresh.IsCompleted.Should().BeFalse();
        metadata.IsCompleted.Should().BeFalse();
        h.Log.Should().Equal("write:k=v");
        gate.SetResult(true);
        await Task.WhenAll(refresh, metadata);
        await h.SettledAsync();
    }

    [Fact]
    public async Task Background_sets_of_one_key_reach_the_inner_tier_in_the_order_they_were_issued()
    {
        using var h = new Rig();
        var first = h.Gate("k", "a");

        await h.Cache.SetAsync<string>("k", Fields("a"), Background, Ct);
        await h.Cache.SetAsync<string>("k", Fields("b"), Background, Ct);

        h.Log.Should().Equal("write:k=a");
        first.SetResult(true);
        await h.SettledAsync();
        h.Log.Should().Equal("write:k=a", "event:k", "write:k=b", "event:k");
    }

    [Fact]
    public async Task An_awaited_set_after_a_background_set_of_the_key_is_written_after_it()
    {
        using var h = new Rig();
        var first = h.Gate("k", "a");
        await h.Cache.SetAsync<string>("k", Fields("a"), Background, Ct);

        var second = h.Cache.SetAsync<string>("k", Fields("b"), Awaited, Ct).AsTask();

        await Task.Delay(100, Ct);
        second.IsCompleted.Should().BeFalse();
        h.Log.Should().Equal("write:k=a");
        first.SetResult(true);
        (await second).Should().BeTrue();
        h.Log.Should().Equal("write:k=a", "event:k", "write:k=b", "event:k");
    }

    [Fact]
    public async Task An_awaited_set_landing_after_a_newer_background_set_leaves_the_newer_hash_local()
    {
        using var h = new Rig();
        await h.ActivateAsync();
        var first = h.Gate("k", "v1");
        var awaited = h.Cache.SetAsync<string>("k", Fields("v1"), Awaited, Ct).AsTask();
        await h.UntilAsync(() => h.Log.Contains("write:k=v1"));

        await h.Cache.SetAsync<string>("k", Fields("v2"), Background, Ct);
        first.SetResult(true);
        (await awaited).Should().BeTrue();
        await h.SettledAsync();

        h.Local("k").Should().Be("v2");
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Local("k").Should().Be("v2");
    }

    [Fact]
    public async Task A_read_of_the_inner_tier_landing_after_a_background_set_leaves_the_set_hash_local()
    {
        using var h = new Rig();
        var reading = new TaskCompletionSource<ICacheEntry<IDictionary<string, string?>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        h.OnRead = _ => Interlocked.Increment(ref started) == 1 ? reading.Task : null;
        var read = h.Cache.GetAsync<string>("k", Awaited, Ct).AsTask();
        await h.UntilAsync(() => Volatile.Read(ref started) == 1);

        await h.Cache.SetAsync<string>("k", Fields("v2"), Background, Ct);
        reading.SetResult(new TestCacheEntry<IDictionary<string, string?>> { Value = Fields("v1"), Expiration = h.Clock.GetUtcNow().AddHours(1) });
        await read;
        await h.SettledAsync();

        h.Local("k").Should().Be("v2");
    }

    [Fact]
    public async Task A_drain_that_gives_up_counts_an_inner_write_answering_its_cancel_with_false_as_drain_cancelled()
    {
        using var h = new Rig(new InMemoryRedisCacheOptions { BackgroundWriteDrainTimeout = TimeSpan.FromMilliseconds(200) });
        h.OnWrite = static async token =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
            }

            return false;
        };
        await h.Cache.SetAsync<string>("k", Fields("v"), Background, Ct);
        await h.UntilAsync(() => h.Log.Contains("write:k=v"));

        await h.Cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await h.SettledAsync();

        h.Telemetry.Received(1).TrackEvent("cache.background_write.failed", Arg.Is<TelemetryTags<string>>(t => t.Any(x => x.Key == "stage" && x.Value == "drain_cancelled")), Arg.Any<TelemetryTags<double>>());
        h.Telemetry.DidNotReceive().TrackEvent("cache.background_write.failed", Arg.Is<TelemetryTags<string>>(t => t.Any(x => x.Key == "stage" && x.Value == "write")), Arg.Any<TelemetryTags<double>>());
    }

    [Fact]
    public async Task A_set_finding_no_place_free_is_written_in_the_foreground_and_the_zero_limit_turns_background_off()
    {
        using var full = new Rig(new InMemoryRedisCacheOptions { MaxBackgroundWrites = 1 });
        var first = full.Gate("k", "a");
        var other = full.Gate("other");
        await full.Cache.SetAsync<string>("k", Fields("a"), Background, Ct);

        var overflow = full.Cache.SetAsync<string>("other", Fields("x"), Background, Ct).AsTask();

        await Task.Delay(100, Ct);
        full.Log.Should().Equal("write:k=a", "write:other=x");
        overflow.IsCompleted.Should().BeFalse();
        other.SetResult(true);
        (await overflow).Should().BeTrue();
        first.SetResult(true);
        await full.SettledAsync();

        using var off = new Rig(new InMemoryRedisCacheOptions { MaxBackgroundWrites = 0 });
        var gate = off.Gate("k");
        var set = off.Cache.SetAsync<string>("k", Fields("v"), Background, Ct).AsTask();
        await Task.Delay(100, Ct);
        set.IsCompleted.Should().BeFalse();
        gate.SetResult(true);
        (await set).Should().BeTrue();
    }

    [Fact]
    public async Task The_places_are_shared_with_the_single_key_cache_options_not_the_cache()
    {
        using var h = new Rig(new InMemoryRedisCacheOptions { MaxBackgroundWrites = 1 });
        var one = h.Gate("one");
        var two = h.Gate("two");

        await h.Cache.SetAsync<string>("one", Fields("a"), Background, Ct);
        var second = h.Cache.SetAsync<string>("two", Fields("b"), Background, Ct).AsTask();

        await Task.Delay(100, Ct);
        second.IsCompleted.Should().BeFalse("the one place of this cache is taken");
        two.SetResult(true);
        await second;
        one.SetResult(true);
        await h.SettledAsync();
    }

    [Fact]
    public async Task The_background_write_does_not_use_the_callers_token()
    {
        using var h = new Rig();
        var gate = h.Gate("k");
        CancellationToken seen = default;
        h.OnToken = token => seen = token;
        using var caller = new CancellationTokenSource();

        await h.Cache.SetAsync<string>("k", Fields("v"), Background, caller.Token);
        await caller.CancelAsync();
        gate.SetResult(true);
        await h.SettledAsync();

        seen.Should().NotBe(caller.Token);
        seen.IsCancellationRequested.Should().BeFalse();
        h.Log.Should().Equal("write:k=v", "event:k");
    }

    [Fact]
    public async Task Disposing_the_cache_waits_for_the_background_writes_and_a_stuck_one_is_cancelled_after_the_timeout()
    {
        using var waiting = new Rig();
        var gate = waiting.Gate("k");
        await waiting.Cache.SetAsync<string>("k", Fields("v"), Background, Ct);

        var dispose = waiting.Cache.DisposeAsync().AsTask();
        var syncDispose = Task.Run(waiting.Cache.Dispose, Ct);

        await Task.Delay(100, Ct);
        dispose.IsCompleted.Should().BeFalse();
        syncDispose.IsCompleted.Should().BeFalse();
        gate.SetResult(true);
        await dispose;
        await syncDispose;
        waiting.Log.Should().Equal("write:k=v", "event:k");

        using var stuck = new Rig(new InMemoryRedisCacheOptions { BackgroundWriteDrainTimeout = TimeSpan.FromMilliseconds(200) });
        var never = stuck.Gate("k");
        CancellationToken seen = default;
        stuck.OnToken = token => seen = token;
        await stuck.Cache.SetAsync<string>("k", Fields("v"), Background, Ct);

        await stuck.Cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        seen.IsCancellationRequested.Should().BeTrue();
        never.SetResult(false);
        await stuck.SettledAsync();
    }

    [Fact]
    public async Task A_fill_returns_with_the_hash_local_while_its_write_is_pending_and_publishes_no_event()
    {
        using var h = new Rig();
        var gate = h.Gate("k");

        (await h.Cache.GetOrAddAsync<string>("k", static _ => Task.FromResult<IDictionary<string, string?>>(Fields("v")), Background, Ct))["f"].Should().Be("v");

        h.Log.Should().Equal("write:k=v");
        h.Local("k").Should().Be("v");
        gate.SetResult(true);
        await h.SettledAsync();
        h.Log.Should().Equal("write:k=v");
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Local("k").Should().Be("v");
    }

    [Fact]
    public async Task A_fill_without_the_flag_waits_for_its_write()
    {
        using var h = new Rig();
        var gate = h.Gate("k");

        var fill = h.Cache.GetOrAddAsync<string>("k", static _ => Task.FromResult<IDictionary<string, string?>>(Fields("v")), Awaited, Ct).AsTask();

        await Task.Delay(100, Ct);
        fill.IsCompleted.Should().BeFalse();
        gate.SetResult(true);
        (await fill)["f"].Should().Be("v");
    }

    [Fact]
    public async Task A_fill_the_inner_tier_refuses_stays_local_for_the_cap_only()
    {
        using var h = new Rig { WriteResult = false };

        await h.Cache.GetOrAddAsync<string>("k", static _ => Task.FromResult<IDictionary<string, string?>>(Fields("v")), Background, Ct);
        await h.SettledAsync();

        h.Clock.Advance(TimeSpan.FromSeconds(29));
        h.Local("k").Should().Be("v");
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Local("k").Should().BeNull();
    }

    [Fact]
    public async Task A_fill_under_the_distributed_lock_is_written_in_the_foreground()
    {
        var locks = Substitute.For<IDistributedLock>();
        locks.AcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IAsyncDisposable>(Substitute.For<IAsyncDisposable>()));
        using var h = new Rig(distributedLock: locks);
        var gate = h.Gate("k");
        var locked = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(10), BackgroundDistributedWrite = true, Lock = new LockProfile { DistributedLockEnabled = true } };

        var fill = h.Cache.GetOrAddAsync<string>("k", static _ => Task.FromResult<IDictionary<string, string?>>(Fields("v")), locked, Ct).AsTask();

        await Task.Delay(100, Ct);
        fill.IsCompleted.Should().BeFalse();
        gate.SetResult(true);
        await fill;
    }

    [Fact]
    public async Task The_distributed_cache_adapter_writes_in_the_foreground_whatever_the_policy_says()
    {
        using var h = new Rig();
        var adapter = new UiPath.Caching.Distributed.UiPathDistributedCache(h.Cache, new UiPath.Caching.Distributed.UiPathDistributedCacheOptions(), new DefaultCacheKeyStrategy(), Background, NullLogger.Instance, TimeProvider.System, tierRetainsValues: true);
        var gate = h.GateAny();

        var set = adapter.SetAsync("k", [1, 2, 3], new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, Ct);

        await Task.Delay(100, Ct);
        h.Log.Should().Equal("write:k=payload");
        set.IsCompleted.Should().BeFalse("its payload may be memory the caller reclaims on return");
        gate.SetResult(true);
        await set;
    }

    [Fact]
    public async Task The_distributed_cache_adapter_is_foreground_even_over_a_default_policy_that_turns_background_on()
    {
        var factory = Substitute.For<ICachePolicyFactory>();
        factory.Default.Returns(new CachePolicy { BackgroundDistributedWrite = true });
        factory.Resolve(Arg.Any<string>()).Returns(new CachePolicy { BackgroundDistributedWrite = true });
        factory.Keys.Returns([]);
        var wrapped = new ForegroundOnlyPolicyFactory(factory);

        wrapped.Default!.BackgroundDistributedWrite.Should().BeFalse();
        wrapped.Resolve("anything")!.BackgroundDistributedWrite.Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public void The_foreground_only_factory_merges_each_policy_once_until_the_inner_one_changes()
    {
        var factory = Substitute.For<ICachePolicyFactory>();
        var first = new CachePolicy { BackgroundDistributedWrite = true };
        factory.Default.Returns(first);
        factory.Resolve("p").Returns(first);
        var wrapped = new ForegroundOnlyPolicyFactory(factory);

        wrapped.Default.Should().BeSameAs(wrapped.Default);
        wrapped.Resolve("p").Should().BeSameAs(wrapped.Resolve("p"));

        var reloaded = new CachePolicy { BackgroundDistributedWrite = true, LocalExpiration = TimeSpan.FromSeconds(7) };
        factory.Default.Returns(reloaded);
        factory.Resolve("p").Returns(reloaded);

        wrapped.Default!.LocalExpiration.Should().Be(TimeSpan.FromSeconds(7));
        wrapped.Resolve("p")!.LocalExpiration.Should().Be(TimeSpan.FromSeconds(7));
        wrapped.Resolve("p")!.BackgroundDistributedWrite.Should().BeFalse();
    }

    private static Dictionary<string, string?> Fields(string value) => new() { ["f"] = value };

    private sealed class RecordingTopic(Rig rig) : ITopic<ICacheEvent>
    {
        public TopicKey TopicKey { get; } = new("topic");

        public EventHandler? OnDisposed { get; set; }

        public IDisposable Subscribe(IObserver<ICacheEvent> observer) => new Nothing();

        public ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default) => rig.Publish(@event);

        public void Dispose()
        {
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Rig : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<bool>> _gates = [];
        private readonly Dictionary<string, TaskCompletionSource<bool>> _gated = [];

        public Rig(InMemoryRedisCacheOptions? options = null, IDistributedLock? distributedLock = null)
        {
            Clock = new StepClock();
            options ??= new InMemoryRedisCacheOptions();
            Telemetry = Substitute.For<ICachingTelemetryProvider>();
            var inner = Substitute.For<IHashCache>();
            ValueTaskStubs.Returns(
                inner.SetAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    var key = call.ArgAt<CacheKey>(0).Name;
                    var values = call.ArgAt<IDictionary<string, string?>>(1);
                    var value = values.TryGetValue("f", out var f) ? f : "payload";
                    Add($"write:{key}={value}");
                    OnToken?.Invoke(call.ArgAt<CancellationToken>(4));
                    if (OnWrite is { } write)
                    {
                        return new ValueTask<bool>(write(call.ArgAt<CancellationToken>(4)));
                    }

                    return WriteThrows ? throw new InvalidOperationException("redis is down") : new ValueTask<bool>(Complete($"{key}={value}", key));
                });
            ValueTaskStubs.Returns(
                inner.SetAsync<ReadOnlyMemory<byte>>(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, ReadOnlyMemory<byte>>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    var key = call.ArgAt<CacheKey>(0).Name;
                    Add($"write:{key}=payload");
                    return new ValueTask<bool>(Complete($"{key}=payload", key));
                });
            ValueTaskStubs.Returns(
                inner.RemoveAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    Add($"remove:{call.ArgAt<CacheKey>(0).Name}");
                    return new ValueTask<bool>(true);
                });
            ValueTaskStubs.Returns(
                inner.RefreshAsync<string>(Arg.Any<CacheKey>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    Add($"refresh:{call.ArgAt<CacheKey>(0).Name}");
                    return new ValueTask<bool>(true);
                });
            ValueTaskStubs.Returns(
                inner.SetMetadataAsync<string>(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, string?>>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    Add($"metadata:{call.ArgAt<CacheKey>(0).Name}");
                    return new ValueTask<bool>(true);
                });
            ValueTaskStubs.Returns(
                inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call => OnRead?.Invoke(call.ArgAt<CacheKey>(0).Name) is { } read
                    ? new ValueTask<ICacheEntry<IDictionary<string, string?>>>(read)
                    : new ValueTask<ICacheEntry<IDictionary<string, string?>>>(new TestCacheEntry<IDictionary<string, string?>> { Value = null, Expiration = DateTimeOffset.MinValue }));
            var events = Substitute.For<ICacheEventFactory>();
            events.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
                .Returns(call => new TestCacheEvent { Type = call.ArgAt<string>(1), Data = call.ArgAt<CacheEventData>(2), Source = new Uri("urn:test") });
            var provider = Substitute.For<ITopicProvider>();
            provider.Enabled.Returns(true);
            provider.Create(Arg.Any<TopicKey>()).Returns(new RecordingTopic(this));
            var topics = Substitute.For<ITopicFactory>();
            topics.Get(Arg.Any<string?>()).Returns(provider);
            Cache = RunTables.Track(new MultilayerHashCache(
                KnownCacheProviderNames.InMemoryRedis,
                inner,
                new MemoryCacheFactory(Clock, NullLoggerFactory.Instance),
                NullChangeTokenFactory.Instance,
                topics,
                events,
                Telemetry,
                options,
                options,
                new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = false },
                distributedLock ?? NullDistributedLock.Instance,
                NullCachePolicyFactory.Instance,
                Clock,
                NullLogger.Instance));
        }

        public StepClock Clock { get; }

        public MultilayerHashCache Cache { get; }

        public ICachingTelemetryProvider Telemetry { get; }

        public List<string> Log { get; } = [];

        public List<bool> WrittenWhenPublished { get; } = [];

        public bool WriteResult { get; set; } = true;

        public bool WriteThrows { get; set; }

        public bool PublishResult { get; set; } = true;

        public Action<CancellationToken>? OnToken { get; set; }

        public Func<CancellationToken, Task<bool>>? OnWrite { get; set; }

        public Func<string, Task<ICacheEntry<IDictionary<string, string?>>>?>? OnRead { get; set; }

        public string? Local(string key) =>
            Cache.GetAsync<string>(key, Awaited, Ct).AsTask().GetAwaiter().GetResult() is { } values && values.TryGetValue("f", out var f) ? f : null;

        public TaskCompletionSource<bool> Gate(string key, string? value = null)
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _gates.Add(gate);
                _gated[value is null ? key : $"{key}={value}"] = gate;
            }

            return gate;
        }

        public TaskCompletionSource<bool> GateAny()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _gates.Add(gate);
                _gated["*"] = gate;
            }

            return gate;
        }

        public void ReleaseAll()
        {
            lock (_gate)
            {
                foreach (var gate in _gates)
                {
                    gate.TrySetResult(true);
                }
            }
        }

        public ValueTask<bool> Publish(ICacheEvent @event)
        {
            lock (_gate)
            {
                WrittenWhenPublished.Add(Log.Count > 0 && Log[^1].StartsWith("write:", StringComparison.Ordinal));
                Log.Add($"event:{@event.Data?.Key}");
            }

            return new ValueTask<bool>(PublishResult);
        }

        public async Task UntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                (DateTime.UtcNow < deadline).Should().BeTrue("the condition was not met in time: {0}", string.Join(", ", Log));
                await Task.Delay(5, Ct);
            }
        }

        public Task SettledAsync() => UntilAsync(() => Cache.RunsInFlight == 0);

        public async Task ActivateAsync()
        {
            await Cache.SetAsync<string>("warm-up", Fields("x"), Background, Ct);
            await UntilAsync(() => Cache.RunsInFlight == 0);
            lock (_gate)
            {
                Log.Clear();
            }
        }

        public void Dispose() => Cache.Dispose();

        private void Add(string entry)
        {
            lock (_gate)
            {
                Log.Add(entry);
            }
        }

        private Task<bool> Complete(string keyAndValue, string key)
        {
            TaskCompletionSource<bool>? gate;
            lock (_gate)
            {
                gate = _gated.GetValueOrDefault(keyAndValue) ?? _gated.GetValueOrDefault(key) ?? _gated.GetValueOrDefault("*");
            }

            return gate is null ? Task.FromResult(WriteResult) : gate.Task.ContinueWith(_ => WriteResult, TaskScheduler.Default);
        }
    }
}
