using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests;

/// <summary>Fail-safe across nodes that share an inner tier and a bus: a peer's removal or write is never answered with the older value, and a fallback never reaches the inner tier.</summary>
public class FailSafePeerTests
{
    private static readonly CacheKey Key = new("k");
    private static readonly TimeSpan LocalLifetime = TimeSpan.FromMilliseconds(100);
    private static readonly CachePolicy FailSafe = new() { FailSafeEnabled = true, LocalExpiration = LocalLifetime, FailSafeCooldown = TimeSpan.Zero };
    private static readonly CachePolicy FailSafeNoRefresh = new() { FailSafeEnabled = true, LocalExpiration = LocalLifetime, FailSafeCooldown = TimeSpan.Zero, RefreshLocalOnPeerWrite = false };

    /// <summary>Every node's clock but the racing test's, so local expiry and the stale age move only when a test advances it.</summary>
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_last_value_is_returned_when_the_inner_tier_and_the_generator_both_miss()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        await WarmAsync(a, "v1");
        await ExpireAsync(peers);

        (await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct)).Should().Be("v1");
    }

    [Fact]
    public async Task A_peers_removal_is_never_answered_with_the_older_value()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var b = peers.Add("b", clock: _clock, localClock: _clock);
        await WarmAsync(a, "v1");

        await b.Cache.RemoveAsync<string>(Key, Ct);
        _clock.Advance(LocalLifetime * 2);

        var act = async () => await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_peers_removal_after_the_value_expired_locally_is_heard_too()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var b = peers.Add("b", clock: _clock, localClock: _clock);
        await WarmAsync(a, "v1");
        _clock.Advance(LocalLifetime * 2);
        (await a.Cache.GetAsync<string>(Key, FailSafe, Ct)).Should().Be("v1", "the inner tier still has it");
        _clock.Advance(LocalLifetime * 2);

        await b.Cache.RemoveAsync<string>(Key, Ct);

        var act = async () => await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct);
        await act.Should().ThrowAsync<InvalidOperationException>("the local entry and its token were gone, but the store still heard the removal");
    }

    [Fact]
    public async Task A_peers_write_is_never_answered_with_the_older_value()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var b = peers.Add("b", clock: _clock, localClock: _clock);
        await WarmAsync(a, "v1");

        await b.Cache.SetAsync(Key, "v2", FailSafe, Ct);
        await a.Settled();
        await ExpireAsync(peers);

        string? value = null;
        try
        {
            value = await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct);
        }
        catch (InvalidOperationException)
        {
            // Dropping the older value is as good as replacing it.
        }

        value.Should().NotBe("v1");
    }

    [Fact]
    public async Task Without_peer_refresh_the_unmarked_value_is_still_kept()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        (await a.Cache.GetOrAddAsync(Key, _ => Task.FromResult<string?>("v1"), FailSafeNoRefresh, Ct)).Should().Be("v1");
        await a.Settled();
        await ExpireAsync(peers);

        (await a.Cache.GetOrAddAsync(Key, Throwing, FailSafeNoRefresh, Ct)).Should().Be("v1", "skipping the read mark does not skip the fail-safe keep");
    }

    [Fact]
    public async Task Without_peer_refresh_a_peers_write_is_never_answered_with_the_older_value()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var b = peers.Add("b", clock: _clock, localClock: _clock);
        (await a.Cache.GetOrAddAsync(Key, _ => Task.FromResult<string?>("v1"), FailSafeNoRefresh, Ct)).Should().Be("v1");
        await a.Settled();

        await b.Cache.SetAsync(Key, "v2", FailSafeNoRefresh, Ct);
        await a.Settled();
        await ExpireAsync(peers);

        (await TryGetOrAddAsync(a, FailSafeNoRefresh)).Should().NotBe("v1", "the peer's write dropped the local copy and its fail-safe value");
    }

    [Fact]
    public async Task A_peers_removal_heard_while_the_value_commits_is_not_kept()
    {
        using var peers = new CachePeers { AutoDeliver = false };
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var b = peers.Add("b", clock: _clock, localClock: _clock);
        await b.Cache.RemoveAsync<string>(Key, Ct);
        var removal = b.Published.Single();
        a.BeforeCommit = _ =>
        {
            a.BeforeCommit = null;
            peers.Deliver(a, removal);
        };

        (await a.Cache.GetOrAddAsync(Key, static _ => Task.FromResult<string?>("v1"), FailSafe, Ct)).Should().Be("v1");
        await a.Settled();
        await ExpireAsync(peers);

        var act = async () => await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct);
        await act.Should().ThrowAsync<InvalidOperationException>("the removal expired the entry before the store watched the topic");
    }

    [Fact]
    public async Task The_nodes_own_events_keep_its_value()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        (await a.Cache.SetAsync(Key, "mine", FailSafe, Ct)).Should().BeTrue();
        a.Published.Should().NotBeEmpty("the set was announced and handed back to the node, which ignores it");
        await ExpireAsync(peers);

        (await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct)).Should().Be("mine");
    }

    [Fact]
    public async Task Lost_events_drop_every_kept_value()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        await WarmAsync(a, "v1");

        a.Subject.Invalidate(MissedEventsReason.Lost);
        await ExpireAsync(peers);

        var act = async () => await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_topic_that_ended_drops_every_kept_value_and_keeps_no_more()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        await WarmAsync(a, "v1");

        a.Subject.OnCompleted();
        await ExpireAsync(peers);

        var act = async () => await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct);
        await act.Should().ThrowAsync<InvalidOperationException>("the store no longer hears peers' changes");
        await WarmAsync(a, "v2");
        await ExpireAsync(peers);
        await act.Should().ThrowAsync<InvalidOperationException>("a topic that completes as it subscribes is not watched, so nothing is kept");
    }

    [Fact]
    public async Task A_fallback_is_neither_written_to_the_inner_tier_nor_announced()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var a = peers.Add("a", telemetry: telemetry, clock: _clock, localClock: _clock);
        await WarmAsync(a, "v1");
        await ExpireAsync(peers);
        var announced = a.Published.Count;

        (await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct)).Should().Be("v1");
        await a.Settled();

        peers.Store.Values.Should().BeEmpty();
        a.Published.Should().HaveCount(announced);
        telemetry.Events.Should().Contain(e => e.Name == "cache.fail_safe.served");
    }

    [Fact]
    public async Task The_cooldown_reads_neither_the_inner_tier_nor_the_generator()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var policy = new CachePolicy { FailSafeEnabled = true, LocalExpiration = LocalLifetime, FailSafeCooldown = TimeSpan.FromMinutes(1) };
        await WarmAsync(a, "v1");
        await ExpireAsync(peers);
        (await a.Cache.GetOrAddAsync(Key, Throwing, policy, Ct)).Should().Be("v1");
        var reads = a.Inner.SingleReads;

        (await a.Cache.GetOrAddAsync(Key, Throwing, policy, Ct)).Should().Be("v1");

        a.Inner.SingleReads.Should().Be(reads);
    }

    [Fact]
    public async Task A_value_generated_while_disconnected_is_kept()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        a.Inner.IsConnected = false;
        (await a.Cache.GetOrAddAsync(Key, static _ => Task.FromResult<string?>("offline"), FailSafe, Ct)).Should().Be("offline");
        _clock.Advance(LocalLifetime * 2);

        (await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct)).Should().Be("offline");
    }

    [Fact]
    public async Task A_late_value_is_written_to_the_inner_tier_and_announced()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var policy = new CachePolicy { FailSafeEnabled = true, LocalExpiration = LocalLifetime, FailSafeTimeout = TimeSpan.FromMilliseconds(50) };
        await WarmAsync(a, "v1");
        await ExpireAsync(peers);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _clock.HasTimers.Should().BeFalse("the only timer below is the soft timeout's");
        var call = a.Cache.GetOrAddAsync(Key, _ => release.Task, policy, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(TimeSpan.FromSeconds(1));

        (await call).Should().Be("v1");
        var announced = a.Published.Count;
        release.SetResult("v2");
        await CachePeers.Until(() => a.Cache.LateGenerations == 0);
        await a.Settled();

        peers.Store.Values.Values.Should().ContainSingle().Which.Should().Be("v2");
        (await a.Cache.GetAsync<string>(Key, policy, Ct)).Should().Be("v2");
        a.Published.Skip(announced).Should().ContainSingle("the late write tells the other nodes").Which.Type.Should().Be(KnownEventTypes.CacheSet);
    }

    [Fact]
    public async Task A_late_value_ends_the_cooldown_of_a_peer_that_fell_back()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var b = peers.Add("b", clock: _clock, localClock: _clock);
        var cooling = new CachePolicy { FailSafeEnabled = true, LocalExpiration = LocalLifetime, FailSafeCooldown = TimeSpan.FromMinutes(1) };
        var soft = new CachePolicy { FailSafeEnabled = true, LocalExpiration = LocalLifetime, FailSafeTimeout = TimeSpan.FromMilliseconds(50) };
        await WarmAsync(a, "v1");
        (await b.Cache.GetOrAddAsync(Key, Throwing, cooling, Ct)).Should().Be("v1", "the inner tier has it");
        await b.Settled();
        await ExpireAsync(peers);
        (await b.Cache.GetOrAddAsync(Key, Throwing, cooling, Ct)).Should().Be("v1", "b falls back, and its cooldown begins");
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var call = a.Cache.GetOrAddAsync(Key, _ => release.Task, soft, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await call).Should().Be("v1");
        release.SetResult("v2");
        await CachePeers.Until(() => a.Cache.LateGenerations == 0);
        await a.Settled();
        await b.Settled();

        (await b.Cache.GetOrAddAsync(Key, Throwing, cooling, Ct)).Should().Be("v2", "the announcement ended b's cooldown, so it read the inner tier");
    }

    [Fact]
    public async Task A_late_value_that_loses_to_a_removal_is_neither_written_nor_announced()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var policy = new CachePolicy { FailSafeEnabled = true, LocalExpiration = LocalLifetime, FailSafeTimeout = TimeSpan.FromMilliseconds(50) };
        await WarmAsync(a, "v1");
        await ExpireAsync(peers);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = a.Cache.GetOrAddAsync(Key, _ => release.Task, policy, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await call).Should().Be("v1");
        var announced = a.Published.Count;

        // The removal lands after the late generator saw its value current and before its write.
        a.Cache.LateStoreStarting = () => a.Cache.RemoveAsync<string>(Key, Ct).AsTask().GetAwaiter().GetResult();
        release.SetResult("late");
        await CachePeers.Until(() => a.Cache.LateGenerations == 0);
        await a.Settled();

        peers.Store.Values.Should().BeEmpty("the write was refused in the key's turn");
        a.Published.Skip(announced).Should().ContainSingle("only the removal is announced").Which.Type.Should().Be(KnownEventTypes.CacheRemoved);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task A_subscription_gap_while_a_value_is_written_leaves_it_out_of_the_kept_values(bool asKept, bool keptBefore)
    {
        var sizes = new CountingSizes();
        using var peers = new CachePeers();
        var a = peers.Add("a", new InMemoryRedisCacheOptions { ClearLocalOnReconnect = false, SizeLimit = 1000, SizeProvider = sizes }, clock: _clock, localClock: _clock);
        if (keptBefore)
        {
            // Otherwise the write below is the node's first under fail-safe: nothing is kept yet, and nothing watches the topic.
            (await a.Cache.GetOrAddAsync(new CacheKey("other"), _ => Task.FromResult<string?>("o"), FailSafe, Ct)).Should().Be("o");
            await a.Settled();
        }

        // Inside the local entry's sizing, before the keep begins, or as the kept value is committed, after its topic was found watched.
        var gap = () => a.Subject.Invalidate(MissedEventsReason.SubscriptionGap);
        if (asKept)
        {
            a.BeforeKept = gap;
        }
        else
        {
            sizes.At(1, gap);
        }

        await WarmAsync(a, "v1");
        await ExpireAsync(peers);

        var act = async () => await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct);
        await act.Should().ThrowAsync<InvalidOperationException>("a value from before the gap is not trusted, though the local tier kept its copy");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_removal_while_the_generator_runs_is_never_answered_with_the_removed_value(bool byPeer)
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var b = peers.Add("b", clock: _clock, localClock: _clock);
        await WarmAsync(a, "v1");
        await ExpireAsync(peers);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = a.Cache.GetOrAddAsync(Key, _ => release.Task, FailSafe, Ct).AsTask();
        var joined = a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct).AsTask();

        await (byPeer ? b : a).Cache.RemoveAsync<string>(Key, Ct);
        await a.Settled();
        release.SetException(new InvalidOperationException("down"));

        await running.Invoking(r => r).Should().ThrowAsync<InvalidOperationException>("the removal dropped the kept value before the generator failed");
        await joined.Invoking(j => j).Should().ThrowAsync<InvalidOperationException>();
        (await TryGetOrAddAsync(a, FailSafe)).Should().BeNull();
    }

    [Fact]
    public async Task A_late_value_is_discarded_after_a_peers_write()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var b = peers.Add("b", clock: _clock, localClock: _clock);
        var policy = new CachePolicy { FailSafeEnabled = true, LocalExpiration = LocalLifetime, FailSafeTimeout = TimeSpan.FromMilliseconds(50) };
        await WarmAsync(a, "v1");
        await ExpireAsync(peers);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _clock.HasTimers.Should().BeFalse("the only timer below is the soft timeout's");
        var call = a.Cache.GetOrAddAsync(Key, _ => release.Task, policy, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await call).Should().Be("v1");

        await b.Cache.SetAsync(Key, "v2", policy, Ct);
        await a.Settled();
        var announced = a.Published.Count;
        release.SetResult("late");
        await CachePeers.Until(() => a.Cache.LateGenerations == 0);
        await a.Settled();

        a.Published.Should().HaveCount(announced, "a late value that is not written is not announced");
        peers.Store.Values.Values.Should().ContainSingle().Which.Should().Be("v2", "the late value is older than the peer's write");
        (await a.Cache.GetAsync<string>(Key, policy, Ct)).Should().Be("v2");
    }

    [Fact]
    public async Task Disconnected_without_local_only_mode_nothing_falls_back()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", new InMemoryRedisCacheOptions { UseLocalOnlyWhenDisconnected = false }, clock: _clock, localClock: _clock);
        await WarmAsync(a, "v1");
        await ExpireAsync(peers);

        a.Inner.IsConnected = false;

        var act = async () => await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct);
        await act.Should().ThrowAsync<InvalidOperationException>("the local tier is not read while disconnected, and neither is its fail-safe value");
    }

    [Fact]
    public async Task A_value_kept_while_a_peers_removal_is_dispatched_is_dropped_by_its_token()
    {
        using var peers = new CachePeers { AutoDeliver = false };
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var b = peers.Add("b", clock: _clock, localClock: _clock);
        await WarmAsync(a, "v1");
        await b.Cache.RemoveAsync<string>(Key, Ct);
        var removal = b.Published.Single();
        // Told after the store's watcher and before the key's token: the keep lands between them.
        using var writer = a.Subject.Subscribe(new KeepingFirstObserver(() => a.Cache.SetAsync(Key, "mid", FailSafe, Ct).AsTask().GetAwaiter().GetResult()));

        peers.Deliver(a, removal);
        await ExpireAsync(peers);

        var act = async () => await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct);
        await act.Should().ThrowAsync<InvalidOperationException>("the removal reached the entry's token after the keep");
    }

    [Fact]
    public async Task A_value_that_skips_fail_safe_is_not_kept_by_a_background_write()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var policy = new CachePolicy { FailSafeEnabled = true, LocalExpiration = LocalLifetime, FailSafeCooldown = TimeSpan.Zero, BackgroundDistributedWrite = true };
        await WarmAsync(a, "v1");
        await ExpireAsync(peers);

        (await a.Cache.GetOrAddWithExpirationAsync(Key, static _ => Task.FromResult(new GeneratedValue<string>("placeholder") { SkipFailSafe = true }), policy, Ct)).Should().Be("placeholder");
        await a.Settled();
        await ExpireAsync(peers);

        var act = async () => await a.Cache.GetOrAddAsync(Key, Throwing, policy, Ct);
        await act.Should().ThrowAsync<InvalidOperationException>("neither the placeholder nor the value before it is kept");
    }

    [Fact]
    public async Task A_subscription_gap_drops_kept_values_even_when_the_local_tier_keeps_its_entries()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", new InMemoryRedisCacheOptions { ClearLocalOnReconnect = false }, clock: _clock, localClock: _clock);
        await WarmAsync(a, "v1");

        a.Subject.Invalidate(MissedEventsReason.SubscriptionGap);
        await ExpireAsync(peers);

        var act = async () => await a.Cache.GetOrAddAsync(Key, Throwing, FailSafe, Ct);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static Task<string?> Throwing(CancellationToken token) => Task.FromException<string?>(new InvalidOperationException("down"));

    private static async Task<string?> TryGetOrAddAsync(CachePeers.Node node, CachePolicy policy)
    {
        try
        {
            return await node.Cache.GetOrAddAsync(Key, Throwing, policy, Ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task WarmAsync(CachePeers.Node node, string value)
    {
        (await node.Cache.GetOrAddAsync(Key, _ => Task.FromResult<string?>(value), FailSafe, Ct)).Should().Be(value);
        await node.Settled();
    }

    /// <summary>The value leaves the inner tier quietly and its local copy expires, as both lifetimes ending would.</summary>
    private Task ExpireAsync(CachePeers peers)
    {
        peers.Store.Values.Clear();
        _clock.Advance(LocalLifetime * 2);
        return Task.CompletedTask;
    }

    /// <summary>Sizes every entry as one, and runs a step once, inside the sizing of the given entry counted from when it was set.</summary>
    private sealed class CountingSizes : ICacheEntrySizeProvider
    {
        private Action? _then;
        private int _left;

        public void At(int sizing, Action then)
        {
            _left = sizing;
            _then = then;
        }

        public long GetSize(UiPath.Caching.ICacheEntry entry)
        {
            if (_then is { } then && --_left == 0)
            {
                _then = null;
                then();
            }

            return 1;
        }
    }

    /// <summary>Runs <paramref name="onEvent"/> for each event, among the observers told before the keyed ones.</summary>
    private sealed class KeepingFirstObserver(Action onEvent) : IFirstObserver<ICacheEvent>
    {
        public void OnNext(ICacheEvent value) => onEvent();

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
