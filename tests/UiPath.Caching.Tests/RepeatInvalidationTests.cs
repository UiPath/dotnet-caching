using Microsoft.Extensions.Options;
using UiPath.Caching.Config;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>With <see cref="InMemoryRedisCacheOptions.RepeatInvalidationAfter"/> set, a peer's invalidation of a key drops the local entry again after the delay, so a refill that raced the replication is read again.</summary>
public class RepeatInvalidationTests
{
    private const string Key = "k";
    private static readonly ManualTimeProvider Clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Half = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_by_default_a_remote_removal_drops_the_entry_once()
    {
        using var peers = new CachePeers(Clock);
        var clock = new ManualTimeProvider(Clock.GetUtcNow());
        var (a, b) = await Holding(peers, clock, repeat: null);

        await a.Cache.RemoveAsync<string>(Key, Ct);
        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        await Refill(peers, b);
        clock.Advance(TimeSpan.FromHours(1));

        clock.HasTimers.Should().BeFalse();
        b.Local.TryGetValue(Key, out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_refill_before_the_delay_is_dropped_at_the_delay()
    {
        using var peers = new CachePeers(Clock);
        var clock = new ManualTimeProvider(Clock.GetUtcNow());
        var (a, b) = await Holding(peers, clock, Delay);

        await a.Cache.RemoveAsync<string>(Key, Ct);
        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        clock.Advance(Half);
        (await Refill(peers, b)).Should().Be("old");
        b.Local.TryGetValue(Key, out _).Should().BeTrue();

        clock.Advance(Half - TimeSpan.FromMilliseconds(1));
        b.Local.TryGetValue(Key, out _).Should().BeTrue();
        clock.Advance(TimeSpan.FromMilliseconds(1));

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_refill_after_the_delay_survives()
    {
        using var peers = new CachePeers(Clock);
        var clock = new ManualTimeProvider(Clock.GetUtcNow());
        var (a, b) = await Holding(peers, clock, Delay);

        await a.Cache.RemoveAsync<string>(Key, Ct);
        clock.Advance(Delay + Half);
        await Refill(peers, b);
        clock.Advance(TimeSpan.FromMinutes(1));

        b.Local.TryGetValue(Key, out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_peer_write_schedules_the_repeat()
    {
        using var peers = new CachePeers(Clock);
        var clock = new ManualTimeProvider(Clock.GetUtcNow());
        var (a, b) = await Holding(peers, clock, Delay);

        await a.Cache.SetAsync(Key, "new", null, Ct);
        await b.Settled();
        await Refill(peers, b);
        clock.Advance(Delay);

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_removal_of_another_key_schedules_nothing()
    {
        using var peers = new CachePeers(Clock);
        var clock = new ManualTimeProvider(Clock.GetUtcNow());
        var (a, b) = await Holding(peers, clock, Delay);

        await a.Cache.SetAsync("other", "x", null, Ct);
        await a.Cache.RemoveAsync<string>("other", Ct);
        clock.Advance(Delay);

        clock.HasTimers.Should().BeFalse();
        b.Local.TryGetValue(Key, out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_nodes_own_write_schedules_nothing()
    {
        using var peers = new CachePeers(Clock);
        var clock = new ManualTimeProvider(Clock.GetUtcNow());
        var (_, b) = await Holding(peers, clock, Delay);

        await b.Cache.SetAsync(Key, "mine", null, Ct);
        clock.Advance(Delay * 3);

        clock.HasTimers.Should().BeFalse();
        b.Local.TryGetValue(Key, out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_loss_report_schedules_nothing()
    {
        using var peers = new CachePeers(Clock);
        var clock = new ManualTimeProvider(Clock.GetUtcNow());
        var (_, b) = await Holding(peers, clock, Delay);

        foreach (var observer in b.Subscribers.ToArray().OfType<IMissedEventsObserver>())
        {
            observer.OnEventsMissed(MissedEventsReason.Lost);
        }

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        await Refill(peers, b);
        clock.Advance(Delay * 3);

        clock.HasTimers.Should().BeFalse();
        b.Local.TryGetValue(Key, out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_disposed_cache_runs_no_queued_removal()
    {
        using var peers = new CachePeers(Clock);
        var clock = new ManualTimeProvider(Clock.GetUtcNow());
        var (a, b) = await Holding(peers, clock, Delay);
        await a.Cache.RemoveAsync<string>(Key, Ct);

        b.Cache.Dispose();
        var advance = () => clock.Advance(Delay * 2);

        advance.Should().NotThrow();
        clock.HasTimers.Should().BeFalse();
    }

    [Fact]
    public async Task A_hash_cache_drops_its_copy_again_after_a_peer_write()
    {
        using var cluster = new PeerCluster(Clock);
        var clock = new ManualTimeProvider(Clock.GetUtcNow());
        var writer = cluster.Add("a");
        var peer = cluster.Add("b", clock: clock, repeatInvalidationAfter: Delay);
        var policy = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30), RefreshLocalOnPeerWrite = false };
        await writer.Cache.SetAsync<string>("k", Fields(("a", "1")), policy, Ct);
        await peer.Cache.GetAsync<string>("k", policy, Ct);
        var reads = peer.InnerReads;

        await writer.Cache.SetAsync<string>("k", Fields(("b", "2")), new HashCacheEntryOptions(SetOption: HashCacheSetOption.HashReplace), policy, Ct);
        await peer.Cache.GetAsync<string>("k", policy, Ct);
        peer.InnerReads.Should().Be(reads);

        clock.Advance(Delay);
        await peer.Cache.GetAsync<string>("k", policy, Ct);

        peer.InnerReads.Should().Be(reads + 1);
    }

    [Fact]
    public void Requests_run_in_order_one_delay_after_each()
    {
        var clock = new ManualTimeProvider(Clock.GetUtcNow());
        var removed = new List<string>();
        using var repeats = new RepeatInvalidations(clock, Delay, removed.Add);

        repeats.Schedule("first");
        clock.Advance(Half);
        repeats.Schedule("second");
        clock.Advance(Half);
        removed.Should().Equal("first");
        clock.Advance(Half);
        removed.Should().Equal("first", "second");

        clock.HasTimers.Should().BeFalse();
    }

    [Fact]
    public void A_removal_that_throws_does_not_stop_the_next()
    {
        var clock = new ManualTimeProvider(Clock.GetUtcNow());
        var removed = new List<string>();
        using var repeats = new RepeatInvalidations(clock, Delay, key => { removed.Add(key); throw new InvalidOperationException(); });

        repeats.Schedule("first");
        repeats.Schedule("second");
        var advance = () => clock.Advance(Delay);

        advance.Should().NotThrow();
        removed.Should().Equal("first", "second");
    }

    [Fact]
    public void A_copy_of_the_options_keeps_the_delay()
    {
        var copy = new InMemoryRedisCacheOptions { RepeatInvalidationAfter = Delay }.ShallowCopy();

        copy.RepeatInvalidationAfter.Should().Be(Delay);
        new InMemoryRedisCacheOptions().RepeatInvalidationAfter.Should().BeNull();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1000, true)]
    [InlineData(0, false)]
    [InlineData(-1000, false)]
    public void The_delay_must_be_positive_or_null(int? milliseconds, bool valid)
    {
        var options = new InMemoryRedisCacheOptions { RepeatInvalidationAfter = milliseconds is { } ms ? TimeSpan.FromMilliseconds(ms) : null };

        var result = new InMemoryRedisTimeoutValidator().Validate(name: null, options);

        result.Succeeded.Should().Be(valid);
    }

    private static Dictionary<string, string?> Fields(params (string Name, string Value)[] fields) =>
        fields.ToDictionary(f => f.Name, f => (string?)f.Value);

    /// <summary>Node <c>b</c> holds <c>old</c> in its local tier, as written by <c>a</c>.</summary>
    private static async Task<(CachePeers.Node A, CachePeers.Node B)> Holding(CachePeers peers, ManualTimeProvider clock, TimeSpan? repeat)
    {
        var a = peers.Add("a");
        var b = peers.Add("b", new InMemoryRedisCacheOptions { RepeatInvalidationAfter = repeat }, clock: clock);
        await a.Cache.SetAsync(Key, "old", null, Ct);
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("old");
        b.Local.TryGetValue(Key, out _).Should().BeTrue();
        return (a, b);
    }

    /// <summary>A read of a replica that still has the old value: the local tier takes it again.</summary>
    private static async Task<string?> Refill(CachePeers peers, CachePeers.Node b)
    {
        peers.Store.WriteQuietly(Key, "old");
        return await b.Cache.GetAsync<string>(Key, null, Ct);
    }
}
