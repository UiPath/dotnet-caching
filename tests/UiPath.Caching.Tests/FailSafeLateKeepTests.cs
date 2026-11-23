using UiPath.Caching.Broadcast;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>A fail-safe keep that runs late, through the cache's real commit and token path: an entry that only expired keeps its value; one a peer changed, or a custom one, does not.</summary>
public sealed class FailSafeLateKeepTests
{
    private static readonly CacheKey Key = new("k");
    private static readonly TimeSpan LocalLifetime = TimeSpan.FromMilliseconds(100);
    private static readonly CachePolicy FailSafe = new() { FailSafeEnabled = true, LocalExpiration = LocalLifetime, FailSafeCooldown = TimeSpan.Zero };

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "v1")]
    [InlineData(true, null)]
    public async Task An_entry_that_expires_between_its_commit_and_its_keep_keeps_its_value_unless_it_is_a_custom_entry(bool custom, string? expected)
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", custom ? new InMemoryRedisCacheOptions { EntryFactory = new PlainEntries() } : null, clock: _clock, localClock: _clock);

        // The keep runs late: the entry's own deadline passed between the commit and the keep.
        a.AfterCommit = key =>
        {
            if (key == Key.Name)
            {
                a.AfterCommit = null;
                _clock.Advance(LocalLifetime * 2);
            }
        };

        (await a.Cache.GetOrAddAsync(Key, static _ => Task.FromResult<string?>("v1"), FailSafe, Ct)).Should().Be("v1");
        await a.Settled();
        peers.Store.Values.Clear();
        _clock.Advance(LocalLifetime * 2);

        (await TryAsync(a)).Should().Be(expected, custom ? "a custom entry cannot tell a peer's change from an expiry, so the old rule holds" : "an entry that only expired leaves its value to fail-safe");
    }

    [Fact]
    public async Task A_peers_removal_the_store_hears_before_the_write_and_the_token_after_it_is_not_kept()
    {
        using var peers = new CachePeers { AutoDeliver = false };
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var b = peers.Add("b", clock: _clock, localClock: _clock);

        // A first fail-safe write makes the store watch the topic.
        (await a.Cache.GetOrAddAsync(new CacheKey("warm"), static _ => Task.FromResult<string?>("w"), FailSafe, Ct)).Should().Be("w");
        await b.Cache.RemoveAsync<string>(Key, Ct);
        var removal = b.Published.Single(e => e.Key == Key.Name);

        // The dispatch reaches the store first: its generation moves before the write reads it, with nothing kept to drop.
        var store = a.Subscribers.Single(o => o is IFirstObserver<ICacheEvent> && o.GetType().DeclaringType == typeof(StaleValues));
        peers.Deliver(store, removal);

        // The rest of the dispatch reaches the entry's token while the write commits, after the token subscribed.
        a.BeforeCommit = key =>
        {
            if (key == Key.Name)
            {
                a.BeforeCommit = null;
                peers.Deliver(a.Subscribers[^1], removal);
            }
        };

        (await a.Cache.GetOrAddAsync(Key, static _ => Task.FromResult<string?>("v1"), FailSafe, Ct)).Should().Be("v1");
        await a.Settled();
        peers.Store.Values.Clear();
        _clock.Advance(LocalLifetime * 2);

        (await TryAsync(a)).Should().BeNull("the removal reached the entry's token after the write read the store's generation");
    }

    [Fact]
    public async Task A_peers_change_that_reaches_an_entrys_token_marks_the_entry()
    {
        using var peers = new CachePeers { AutoDeliver = false };
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var b = peers.Add("b", clock: _clock, localClock: _clock);
        (await a.Cache.GetOrAddAsync(Key, static _ => Task.FromResult<string?>("v1"), FailSafe, Ct)).Should().Be("v1");
        a.Local.TryGetValue(Key.Name, out var stored).Should().BeTrue();
        var entry = (ILocalEntry)stored!;
        entry.TokenChanged.Should().BeFalse();

        await b.Cache.RemoveAsync<string>(Key, Ct);
        peers.Deliver(a, b.Published.Single(e => e.Key == Key.Name));

        entry.TokenChanged.Should().BeTrue("the mark is what a keep still in flight reads when no leaving has been heard yet");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_removal_or_write_between_the_first_fail_safe_commit_and_its_keep_refuses_the_keep(bool removal)
    {
        using var peers = new CachePeers();
        using var gate = new ManualResetEventSlim();
        var policies = Substitute.For<ICachePolicyFactory>();
        policies.Keys.Returns(["guarded"]);
        policies.Resolve("guarded").Returns(new CachePolicy { FailSafeEnabled = true });
        var a = peers.Add("a", clock: _clock, localClock: _clock, policies: policies);

        // Leavings are held, so only the generation can refuse the keep; the overlapping change runs in the key's turn, which the writing thread holds.
        a.EvictionGate = gate;
        var plain = new CachePolicy { FailSafeEnabled = false, LocalExpiration = LocalLifetime };
        a.AfterCommit = key =>
        {
            if (key == Key.Name)
            {
                a.AfterCommit = null;
                var change = removal ? a.Cache.RemoveAsync<string>(Key, Ct).AsTask() : a.Cache.SetAsync(Key, "plain", plain, Ct).AsTask();
                change.Wait(TimeSpan.FromSeconds(5), Ct).Should().BeTrue("the change runs in the turn the writing thread holds");
            }
        };

        try
        {
            (await a.Cache.GetOrAddAsync(Key, static _ => Task.FromResult<string?>("v1"), FailSafe, Ct)).Should().Be("v1");
            await a.Settled();
            peers.Store.Values.Clear();
            _clock.Advance(LocalLifetime * 2);

            (await TryAsync(a)).Should().BeNull("the change came after the write read the generation, so its value was never kept");
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task An_entry_that_expires_and_then_a_peers_removal_before_the_keep_refuses_the_keep()
    {
        using var peers = new CachePeers { AutoDeliver = false };
        using var gate = new ManualResetEventSlim();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var b = peers.Add("b", clock: _clock, localClock: _clock);
        (await a.Cache.GetOrAddAsync(new CacheKey("warm"), static _ => Task.FromResult<string?>("w"), FailSafe, Ct)).Should().Be("w");
        await b.Cache.RemoveAsync<string>(Key, Ct);
        var removal = b.Published.Single(e => e.Key == Key.Name);
        var store = a.Subscribers.Single(o => o is IFirstObserver<ICacheEvent> && o.GetType().DeclaringType == typeof(StaleValues));

        // Between the commit and the keep the entry expires, its token is gone, and only the store hears the peer; leavings are held.
        a.EvictionGate = gate;
        a.AfterCommit = key =>
        {
            if (key == Key.Name)
            {
                a.AfterCommit = null;
                _clock.Advance(LocalLifetime * 2);
                peers.Deliver(store, removal);
            }
        };

        try
        {
            (await a.Cache.GetOrAddAsync(Key, static _ => Task.FromResult<string?>("v1"), FailSafe, Ct)).Should().Be("v1");
            await a.Settled();
            peers.Store.Values.Clear();
            _clock.Advance(LocalLifetime * 2);

            (await TryAsync(a)).Should().BeNull("the peer's removal came after the write read the generation");
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task A_cache_configured_with_fail_safe_tracks_local_writes_from_its_construction()
    {
        using var peers = new CachePeers();
        var policies = Substitute.For<ICachePolicyFactory>();
        policies.Default.Returns(new CachePolicy { FailSafeEnabled = true });
        policies.Keys.Returns([]);
        var a = peers.Add("a", clock: _clock, localClock: _clock, policies: policies);

        var store = StoreOf(a.Cache);
        store.Should().NotBeNull("the store exists before the first fail-safe write, so no write that began earlier is untracked");
        var before = store!.ChangeFor(Key.Name);

        (await a.Cache.SetAsync(Key, "plain", new CachePolicy { FailSafeEnabled = false }, Ct)).Should().BeTrue();

        store.ChangeFor(Key.Name).Should().BeGreaterThan(before, "a local write that keeps nothing still refuses an older keep in flight");
    }

    [Fact]
    public async Task A_cache_without_fail_safe_creates_no_store()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);

        (await a.Cache.SetAsync(Key, "plain", null, Ct)).Should().BeTrue();

        StoreOf(a.Cache).Should().BeNull("a cache never given fail-safe pays nothing for it");
    }

    // The store is the cache's own state, read here without a member kept only for tests.
    private static StaleValues? StoreOf(MultilayerCache cache) =>
        (StaleValues?)typeof(MultilayerCacheBase).GetField("_stale", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(cache);

    private static async Task<string?> TryAsync(CachePeers.Node node)
    {
        try
        {
            return await node.Cache.GetOrAddAsync(Key, static _ => Task.FromException<string?>(new InvalidOperationException("down")), FailSafe, Ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Entries of the application's own type: not the cache's, so they carry none of its local marks.</summary>
    private sealed class PlainEntries : ICacheEntryFactory
    {
        public ICacheEntry<T> Create<T>(T value, DateTimeOffset expiration, IDictionary<string, string?>? properties = null) => new PlainEntry<T>(value, expiration, properties);
    }

    private sealed record PlainEntry<T>(T? Value, DateTimeOffset Expiration, IDictionary<string, string?>? Metadata) : ICacheEntry<T>
    {
        object? ICacheEntry.Value => Value;

        public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) =>
            this with { Expiration = expiration ?? Expiration, Metadata = metadata ?? Metadata };
    }
}
