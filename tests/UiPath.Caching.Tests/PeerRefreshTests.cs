using System.Diagnostics;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests;

/// <summary>A peer's write to a key this node holds keeps the held copy and replaces it with a fresh inner read, in the background.</summary>
public class PeerRefreshTests
{
    private const string Key = "k";
    private const string Dropped = "cache.peer_refresh.dropped";
    private const string Kept = "cache.peer_refresh.kept";
    private const string Discarded = "cache.peer_refresh.discarded";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_peer_set_keeps_the_held_value_until_the_refresh_lands()
    {
        using var peers = new CachePeers();
        var (a, b) = await HoldingOld(peers);
        var before = b.Inner.SingleReads;
        var gate = new TaskCompletionSource();
        b.Inner.Hold = gate.Task;

        await a.Cache.SetAsync(Key, "new", null, Ct);
        await Until(() => b.Inner.SingleReads == before + 1);

        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("old");
        b.Inner.SingleReads.Should().Be(before + 1);
        b.Inner.BatchReads.Should().Be(0);

        gate.SetResult();
        await b.Settled();

        b.Local.TryGetValue(Key, out _).Should().BeTrue();
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("new");
        b.Inner.SingleReads.Should().Be(before + 1);
        b.Inner.BatchReads.Should().Be(0);
    }

    [Fact]
    public async Task A_peer_remove_drops_at_once()
    {
        using var peers = new CachePeers();
        var (a, b) = await HoldingOld(peers);
        var before = b.Inner.SingleReads;

        await a.Cache.RemoveAsync<string>(Key, Ct);

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        await b.Settled();
        b.Inner.SingleReads.Should().Be(before);
    }

    [Fact]
    public async Task A_key_gone_from_the_inner_tier_on_refresh_drops()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = await HoldingOld(peers, bTelemetry: telemetry);
        var set = await QuietSet(peers, a);
        peers.Store.RemoveQuietly(Key);

        peers.Deliver(b, set);
        await b.Settled();

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        DroppedReasons(telemetry).Should().Equal("not_found");
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("times_out")]
    public async Task A_failed_refresh_drops_the_held_copy(string how)
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = await HoldingOld(peers, new InMemoryRedisCacheOptions { Timeout = TimeSpan.FromMilliseconds(100) }, telemetry);
        var set = await QuietSet(peers, a);
        var slow = new TaskCompletionSource();
        if (how == "throws")
        {
            b.Inner.ReadThrows = true;
        }
        else
        {
            // A read that never observes its token: only the refresher's own bound ends the wait.
            b.Inner.Hold = slow.Task;
            b.Inner.HoldIgnoresCancellation = true;
        }

        peers.Deliver(b, set);
        await b.Settled();

        b.Cache.RunsInFlight.Should().Be(0);
        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        DroppedReasons(telemetry).Should().Equal(how == "throws" ? "failed" : "timed_out");
        slow.SetResult();
    }

    [Fact]
    public async Task A_peer_batch_is_refreshed_with_one_batched_read()
    {
        using var peers = new CachePeers();
        var keys = new[] { "k1", "k2", "k3", "k4", "k5" };
        var (a, b) = await HoldingOld(peers, keys);
        var singleReads = b.Inner.SingleReads;
        var gate = new TaskCompletionSource();
        b.Cache.PeerRefreshHold = gate.Task;

        await a.Cache.SetAsync([.. keys.Select(k => KeyValuePair.Create<CacheKey, string?>(k, "new-" + k))], null, Ct);
        foreach (var key in keys)
        {
            b.Local.TryGetValue(key, out _).Should().BeTrue();
        }

        gate.SetResult();
        await b.Settled();

        b.Inner.BatchReads.Should().Be(1);
        b.Inner.BatchSizes.Should().Equal(5);
        b.Inner.SingleReads.Should().Be(singleReads);
        foreach (var key in keys)
        {
            b.Local.TryGetValue(key, out _).Should().BeTrue();
            (await b.Cache.GetAsync<string>(key, null, Ct)).Should().Be("new-" + key);
        }
    }

    [Fact]
    public async Task Repeated_peer_sets_before_the_read_coalesce_into_one_read()
    {
        using var peers = new CachePeers();
        var (a, b) = await HoldingOld(peers);
        var before = b.Inner.SingleReads;
        var gate = new TaskCompletionSource();
        b.Cache.PeerRefreshHold = gate.Task;

        foreach (var value in new[] { "new1", "new2", "new3" })
        {
            await a.Cache.SetAsync(Key, value, null, Ct);
        }

        b.Local.TryGetValue(Key, out _).Should().BeTrue();
        gate.SetResult();
        await b.Settled();

        b.Inner.SingleReads.Should().Be(before + 1);
        b.Local.TryGetValue(Key, out _).Should().BeTrue();
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("new3");
    }

    [Fact]
    public async Task A_peer_set_during_the_read_causes_one_more_read()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = await HoldingOld(peers, bTelemetry: telemetry);
        var before = b.Inner.SingleReads;
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        b.Inner.Hold = first.Task;
        await a.Cache.SetAsync(Key, "new", null, Ct);
        await Until(() => b.Inner.SingleReads == before + 1);

        await a.Cache.SetAsync(Key, "newer", null, Ct);
        b.Local.TryGetValue(Key, out _).Should().BeTrue();
        // The first read reached the store before the second write; the second read finds it.
        peers.Store.WriteQuietly(Key, "new");
        b.Inner.Hold = second.Task;
        first.SetResult();
        await Until(() => b.Inner.SingleReads == before + 2);
        peers.Store.WriteQuietly(Key, "newer");
        second.SetResult();
        await b.Settled();

        b.Inner.SingleReads.Should().Be(before + 2);
        b.Local.TryGetValue(Key, out _).Should().BeTrue();
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("newer");
        DroppedReasons(telemetry).Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_still_dirty_after_the_second_read_drops()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = await HoldingOld(peers, bTelemetry: telemetry);
        var before = b.Inner.SingleReads;
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        b.Inner.Hold = first.Task;
        await a.Cache.SetAsync(Key, "new", null, Ct);
        await Until(() => b.Inner.SingleReads == before + 1);
        await a.Cache.SetAsync(Key, "newer", null, Ct);

        b.Inner.Hold = second.Task;
        first.SetResult();
        await Until(() => b.Inner.SingleReads == before + 2);
        await a.Cache.SetAsync(Key, "newest", null, Ct);
        b.Local.TryGetValue(Key, out _).Should().BeTrue();
        second.SetResult();
        await b.Settled();

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        DroppedReasons(telemetry).Should().Equal("still_dirty");
        telemetry.Events.Should().NotContain(e => e.Name == Kept);
    }

    [Fact]
    public async Task A_local_write_during_the_refresh_wins()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = await HoldingOld(peers, bTelemetry: telemetry);
        var before = b.Inner.SingleReads;
        var gate = new TaskCompletionSource();
        b.Inner.Hold = gate.Task;
        await a.Cache.SetAsync(Key, "new", null, Ct);
        await Until(() => b.Inner.SingleReads == before + 1);

        await b.Cache.SetAsync(Key, "mine", null, Ct);
        // The read reached the store before the local write.
        peers.Store.WriteQuietly(Key, "new");
        gate.SetResult();
        await b.Settled();

        b.Local.TryGetValue(Key, out _).Should().BeTrue();
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("mine");
        DiscardedReasons(telemetry).Should().Equal("newer_local");
        DroppedReasons(telemetry).Should().BeEmpty();
    }

    [Fact]
    public async Task A_background_write_in_flight_drops_on_a_peer_s_later_write()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = await HoldingOld(peers, bTelemetry: telemetry);
        var landed = new TaskCompletionSource();
        b.Inner.AfterWrite = landed.Task;
        await b.Cache.SetAsync(Key, "mine", new CachePolicy { BackgroundDistributedWrite = true }, Ct);
        await Until(() => (string?)peers.Store.Values[Key] == "mine");

        // The peer's write lands after this node's, and its event comes while this node's write is still in flight.
        var set = await QuietSet(peers, a);
        peers.Deliver(b, set);
        landed.SetResult();
        await b.Settled();

        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("new");
    }

    [Fact]
    public async Task A_declined_fill_is_requeued_once()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = await HoldingOld(peers, bTelemetry: telemetry);
        var neighbour = Enumerable.Range(0, 100_000).Select(i => $"n{i}").First(n => BackgroundWrites.Stripe(n) == BackgroundWrites.Stripe(Key));
        var before = b.Inner.SingleReads;
        var gate = new TaskCompletionSource();
        b.Inner.Hold = gate.Task;
        await a.Cache.SetAsync(Key, "new", null, Ct);
        await Until(() => b.Inner.SingleReads == before + 1);

        // Moves the key's stripe without touching the key: the first fill is declined while the entry is still held.
        b.Inner.Hold = null;
        await b.Cache.SetAsync(neighbour, "x", null, Ct);
        gate.SetResult();
        await b.Settled();

        b.Inner.SingleReads.Should().Be(before + 2);
        b.Local.TryGetValue(Key, out _).Should().BeTrue();
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("new");
        telemetry.Events.Count(e => e.Name == Kept).Should().Be(1);
        DroppedReasons(telemetry).Should().BeEmpty();
    }

    [Fact]
    public async Task An_event_on_the_replaced_token_drops_the_refreshed_copy()
    {
        using var peers = new CachePeers();
        var (a, b) = await HoldingOld(peers);
        var oldToken = b.Subscribers[^1];
        await a.Cache.SetAsync(Key, "new", null, Ct);
        await b.Settled();
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("new");

        // A write whose dispatch took its observers before the refreshed entry's token subscribed: only the old token hears it.
        peers.AutoDeliver = false;
        await a.Cache.SetAsync(Key, "newer", null, Ct);
        peers.Deliver(oldToken, a.Published[^1]);
        await b.Settled();

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_removal_on_the_replaced_token_drops_the_refreshed_copy()
    {
        using var peers = new CachePeers();
        var (a, b) = await HoldingOld(peers);
        var oldToken = b.Subscribers[^1];
        await a.Cache.SetAsync(Key, "new", null, Ct);
        await b.Settled();
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("new");

        // A removal whose dispatch took its observers before the refreshed entry's token subscribed.
        peers.AutoDeliver = false;
        await a.Cache.RemoveAsync<string>(Key, Ct);
        peers.Deliver(oldToken, a.Published[^1]);
        await b.Settled();

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_removal_reaching_the_new_token_while_the_keep_commits_does_not_hang()
    {
        using var peers = new CachePeers();
        var (a, b) = await HoldingOld(peers);
        var before = b.Inner.SingleReads;
        var subscribed = b.Subscribers.Count;
        var gate = new TaskCompletionSource();
        b.Inner.Hold = gate.Task;
        await a.Cache.SetAsync(Key, "new", null, Ct);
        await Until(() => b.Inner.SingleReads == before + 1);
        peers.AutoDeliver = false;
        await a.Cache.RemoveAsync<string>(Key, Ct);
        var removal = a.Published[^1];
        // The read found the value before the removal.
        peers.Store.WriteQuietly(Key, "new");

        // The keep holds the key's locks while it commits the refreshed entry, whose token the removal reaches first.
        bool? delivered = null;
        b.BeforeCommit = key =>
        {
            if (key == Key && delivered is null && b.Subscribers.Count > subscribed)
            {
                var newToken = b.Subscribers[^1];
                delivered = Task.Run(() => peers.Deliver(newToken, removal), Ct).Wait(TimeSpan.FromSeconds(5), Ct);
            }
        };
        gate.SetResult();
        await Until(() => delivered is not null);
        await b.Settled();

        delivered.Should().BeTrue();
        b.Cache.RunsInFlight.Should().Be(0);
        b.Local.TryGetValue(Key, out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_key_is_read_again_once_in_all_dirty_then_declined_drops()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = await HoldingOld(peers, bTelemetry: telemetry);
        var neighbour = Enumerable.Range(0, 100_000).Select(i => $"n{i}").First(n => BackgroundWrites.Stripe(n) == BackgroundWrites.Stripe(Key));
        var before = b.Inner.SingleReads;
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        b.Inner.Hold = first.Task;
        await a.Cache.SetAsync(Key, "new", null, Ct);
        await Until(() => b.Inner.SingleReads == before + 1);
        await a.Cache.SetAsync(Key, "newer", null, Ct);

        b.Inner.Hold = second.Task;
        first.SetResult();
        await Until(() => b.Inner.SingleReads == before + 2);
        // Declines the second read's fill while the entry is still held.
        await b.Cache.SetAsync(neighbour, "x", null, Ct);
        second.SetResult();
        await b.Settled();

        b.Inner.SingleReads.Should().Be(before + 2);
        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        DroppedReasons(telemetry).Should().Equal("declined");
        telemetry.Events.Should().NotContain(e => e.Name == Kept);
    }

    [Fact]
    public async Task Keys_past_the_queue_cap_drop_at_once()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var keys = new[] { "k1", "k2", "k3" };
        var (a, b) = await HoldingOld(peers, keys, bTelemetry: telemetry);
        b.Cache.PeerRefreshMaxQueued = 2;
        var gate = new TaskCompletionSource();
        b.Cache.PeerRefreshHold = gate.Task;

        foreach (var key in keys)
        {
            await a.Cache.SetAsync(key, "new", null, Ct);
        }

        b.Local.TryGetValue("k1", out _).Should().BeTrue();
        b.Local.TryGetValue("k2", out _).Should().BeTrue();
        b.Local.TryGetValue("k3", out _).Should().BeFalse();
        DroppedReasons(telemetry).Should().Equal("overflow");
        gate.SetResult();
        await b.Settled();
    }

    [Fact]
    public async Task A_key_that_waited_past_the_timeout_drops()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = await HoldingOld(peers, new InMemoryRedisCacheOptions { Timeout = TimeSpan.FromMilliseconds(100) }, telemetry);
        var before = b.Inner.SingleReads;
        b.Cache.PeerRefreshHold = Task.Delay(300, Ct);

        await a.Cache.SetAsync(Key, "new", null, Ct);
        b.Local.TryGetValue(Key, out _).Should().BeTrue();
        await Until(() => DroppedReasons(telemetry).Length > 0);
        await b.Settled();

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        DroppedReasons(telemetry).Should().Equal("expired");
        b.Inner.SingleReads.Should().Be(before);
    }

    [Fact]
    public async Task A_disconnected_inner_tier_drops_on_a_peer_set_without_reading()
    {
        using var peers = new CachePeers();
        var (a, b) = await HoldingOld(peers);
        var set = await QuietSet(peers, a);
        var before = b.Inner.SingleReads;
        b.Inner.IsConnected = false;

        peers.Deliver(b, set);

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        await b.Settled();
        b.Inner.SingleReads.Should().Be(before);
    }

    [Fact]
    public async Task A_policy_that_opts_out_drops_as_before()
    {
        using var peers = new CachePeers();
        var optOut = new CachePolicy { RefreshLocalOnPeerWrite = false };
        var (a, b) = await HoldingOld(peers, policy: optOut);
        var before = b.Inner.SingleReads;

        await a.Cache.SetAsync(Key, "new", null, Ct);

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        await b.Settled();
        b.Inner.SingleReads.Should().Be(before);
    }

    [Fact]
    public async Task The_dispatcher_is_not_held_by_a_slow_refresh()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a");
        var b = peers.Add("b");
        foreach (var key in new[] { "k1", "k2" })
        {
            await a.Cache.SetAsync(key, "old", null, Ct);
            (await b.Cache.GetAsync<string>(key, null, Ct)).Should().Be("old");
        }

        peers.AutoDeliver = false;
        await a.Cache.SetAsync("k1", "new", null, Ct);
        await a.Cache.SetAsync("k2", "new", null, Ct);
        var (set1, set2) = (a.Published[^2], a.Published[^1]);
        b.Inner.Hold = new TaskCompletionSource().Task;

        var watch = Stopwatch.StartNew();
        peers.Deliver(b, set1);
        peers.Deliver(b, set2);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));

        b.Local.TryGetValue("k1", out _).Should().BeTrue();
        b.Local.TryGetValue("k2", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_reads_demand_the_master()
    {
        using var peers = new CachePeers();
        var (a, b) = await HoldingOld(peers);

        await a.Cache.SetAsync(Key, "new", null, Ct);
        await b.Settled();

        b.Inner.ReadsOnMaster.Should().Equal(false, true);
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("new");
    }

    [Fact]
    public async Task A_refresh_read_still_demands_the_master_after_its_first_await()
    {
        using var peers = new CachePeers();
        var (a, b) = await HoldingOld(peers);
        var before = b.Inner.SingleReads;
        var flags = new TaskCompletionSource();
        b.Inner.BeforeFlags = flags.Task;

        await a.Cache.SetAsync(Key, "new", null, Ct);
        await Until(() => b.Inner.SingleReads == before + 1);
        flags.SetResult();
        await b.Settled();

        b.Inner.ReadsOnMaster.Should().Equal(false, true);
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("new");
    }

    [Fact]
    public async Task A_drain_that_throws_keeps_later_refreshes_working()
    {
        using var peers = new CachePeers();
        var (a, b) = await HoldingOld(peers, ["k1", "k2"]);
        b.Inner.ReadThrows = true;
        b.RemoveThrows = true;

        // The read fails, and so does the drop that follows it.
        await a.Cache.SetAsync("k1", "new-k1", null, Ct);
        await b.Settled();
        b.Inner.ReadThrows = false;
        b.RemoveThrows = false;
        var before = b.Inner.SingleReads;

        await a.Cache.SetAsync("k2", "new-k2", null, Ct);
        await b.Settled();

        b.Inner.SingleReads.Should().Be(before + 1);
        b.Local.TryGetValue("k2", out _).Should().BeTrue();
        (await b.Cache.GetAsync<string>("k2", null, Ct)).Should().Be("new-k2");
        b.Cache.RunsInFlight.Should().Be(0);
    }

    [Fact]
    public async Task Disposing_the_cache_stops_queued_refreshes()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = await HoldingOld(peers, bTelemetry: telemetry);
        var before = b.Inner.SingleReads;
        var gate = new TaskCompletionSource();
        b.Cache.PeerRefreshHold = gate.Task;

        await a.Cache.SetAsync(Key, "new", null, Ct);
        b.Cache.Dispose();
        gate.SetResult();
        await b.Settled();

        b.Cache.RunsInFlight.Should().Be(0);
        b.Inner.SingleReads.Should().Be(before);
        telemetry.Events.Should().NotContain(e => e.Name == Kept);
        telemetry.Exceptions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_read_begun_before_a_peer_set_of_a_key_not_held_does_not_keep_the_old_value()
    {
        using var peers = new CachePeers();
        var (a, b) = (peers.Add("a"), peers.Add("b"));
        await a.Cache.SetAsync(Key, "old", null, Ct);
        var gate = new TaskCompletionSource();
        b.Inner.Hold = gate.Task;
        var read = b.Cache.GetAsync<string>(Key, null, Ct).AsTask();
        await Until(() => b.Inner.SingleReads == 1);

        await a.Cache.SetAsync(Key, "new", null, Ct);
        // The read reached the store before the peer's write.
        peers.Store.WriteQuietly(Key, "old");
        gate.SetResult();
        (await read).Should().Be("old");
        peers.Store.WriteQuietly(Key, "new");

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("new");
    }

    [Fact]
    public async Task A_read_begun_before_a_peer_removal_of_a_key_not_held_does_not_keep_the_old_value()
    {
        using var peers = new CachePeers();
        var (a, b) = (peers.Add("a"), peers.Add("b"));
        await a.Cache.SetAsync(Key, "old", null, Ct);
        var gate = new TaskCompletionSource();
        b.Inner.Hold = gate.Task;
        var read = b.Cache.GetAsync<string>(Key, null, Ct).AsTask();
        await Until(() => b.Inner.SingleReads == 1);

        await a.Cache.RemoveAsync<string>(Key, Ct);
        peers.Store.WriteQuietly(Key, "old");
        gate.SetResult();
        (await read).Should().Be("old");
        peers.Store.RemoveQuietly(Key);

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_read_begun_before_lost_events_does_not_keep_its_value()
    {
        using var peers = new CachePeers();
        var (a, b) = (peers.Add("a"), peers.Add("b"));
        await a.Cache.SetAsync(Key, "old", null, Ct);
        var gate = new TaskCompletionSource();
        b.Inner.Hold = gate.Task;
        var read = b.Cache.GetAsync<string>(Key, null, Ct).AsTask();
        await Until(() => b.Inner.SingleReads == 1);

        b.Subject.Invalidate(MissedEventsReason.Lost);
        gate.SetResult();
        (await read).Should().Be("old");

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_peer_set_of_a_key_in_another_stripe_does_not_refuse_the_fill()
    {
        using var peers = new CachePeers();
        var (a, b) = (peers.Add("a"), peers.Add("b"));
        var other = Enumerable.Range(0, 100).Select(i => $"o{i}").First(o => BackgroundWrites.Stripe(o) != BackgroundWrites.Stripe(Key));
        await a.Cache.SetAsync(Key, "old", null, Ct);
        var gate = new TaskCompletionSource();
        b.Inner.Hold = gate.Task;
        var read = b.Cache.GetAsync<string>(Key, null, Ct).AsTask();
        await Until(() => b.Inner.SingleReads == 1);

        await a.Cache.SetAsync(other, "x", null, Ct);
        gate.SetResult();
        (await read).Should().Be("old");

        b.Local.TryGetValue(Key, out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_fill_that_commits_while_a_peer_set_reaches_the_key_s_other_observers_is_not_kept()
    {
        using var peers = new CachePeers();
        var (a, b) = (peers.Add("a"), peers.Add("b"));
        await a.Cache.SetAsync(Key, "old", null, Ct);
        var gate = new TaskCompletionSource();
        b.Inner.Hold = gate.Task;
        var read = b.Cache.GetAsync<string>(Key, null, Ct).AsTask();
        await Until(() => b.Inner.SingleReads == 1);
        peers.AutoDeliver = false;
        await a.Cache.SetAsync(Key, "new", null, Ct);
        peers.Store.WriteQuietly(Key, "old");

        // The dispatch took the key's observers before the fill's token subscribed; the fill commits while it delivers.
        using var stall = b.Subject.Subscribe(new StallingObserver(Key, () =>
        {
            gate.SetResult();
            read.Wait(Ct);
        }));
        peers.Deliver(b, a.Published[^1]);
        (await read).Should().Be("old");
        peers.Store.WriteQuietly(Key, "new");

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("new");
    }

    [Fact]
    public async Task The_cache_watches_its_topic_once_from_its_first_inner_read_until_disposed()
    {
        using var peers = new CachePeers();
        var b = peers.Add("b");
        b.Subscribers.Should().BeEmpty();
        b.Cache.BackgroundWritesActive.Should().BeFalse();

        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().BeNull();
        (await b.Cache.GetAsync<string>("other", null, Ct)).Should().BeNull();

        var guard = b.Subscribers.OfType<IFirstObserver<ICacheEvent>>().Should().ContainSingle().Subject;
        b.Cache.FillsTracked.Should().BeTrue();
        b.Cache.Dispose();
        b.Unsubscribed.Should().Contain(guard);
    }

    [Fact]
    public async Task Watching_counts_changes_for_fills_without_ordering_awaited_writes()
    {
        using var peers = new CachePeers();
        var b = peers.Add("b");

        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().BeNull();
        await b.Cache.SetAsync(Key, "mine", null, Ct);
        await b.Cache.RemoveAsync<string>(Key, Ct);

        b.Cache.FillsTracked.Should().BeTrue();
        b.Cache.BackgroundWritesActive.Should().BeFalse("only a background write turns the write order on");
    }

    [Fact]
    public async Task A_read_begun_before_this_node_s_awaited_write_does_not_replace_it()
    {
        using var peers = new CachePeers();
        var (a, b) = (peers.Add("a"), peers.Add("b"));
        await a.Cache.SetAsync(Key, "old", null, Ct);
        var gate = new TaskCompletionSource();
        b.Inner.Hold = gate.Task;
        var read = b.Cache.GetAsync<string>(Key, null, Ct).AsTask();
        await Until(() => b.Inner.SingleReads == 1);

        await b.Cache.SetAsync(Key, "mine", null, Ct);
        peers.Store.WriteQuietly(Key, "old");
        gate.SetResult();
        (await read).Should().Be("old");
        peers.Store.WriteQuietly(Key, "mine");

        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("mine");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_batch_read_begun_before_a_peer_set_does_not_keep_the_old_value(bool entries)
    {
        using var peers = new CachePeers();
        var (a, b) = (peers.Add("a"), peers.Add("b"));
        await a.Cache.SetAsync(Key, "old", null, Ct);
        await a.Cache.SetAsync("k2", "old2", null, Ct);
        var gate = new TaskCompletionSource();
        b.Inner.Hold = gate.Task;
        CacheKey[] keys = [Key, "k2"];
        var read = entries
            ? b.Cache.GetCacheEntriesAsync<string>(keys, null, Ct).AsTask().ContinueWith(t => t.Result.Select(e => e.Value.Value).ToArray(), TaskScheduler.Default)
            : b.Cache.GetAsync<string>(keys, null, Ct).AsTask().ContinueWith(t => t.Result.Select(e => e.Value).ToArray(), TaskScheduler.Default);
        await Until(() => b.Inner.BatchReads == 1);

        await a.Cache.SetAsync(Key, "new", null, Ct);
        peers.Store.WriteQuietly(Key, "old");
        gate.SetResult();
        (await read).Should().Equal("old", "old2");
        peers.Store.WriteQuietly(Key, "new");

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("new");
    }

    [Fact]
    public async Task A_read_begun_before_a_peer_s_topic_invalidation_does_not_keep_its_value()
    {
        using var peers = new CachePeers();
        var (a, b) = (peers.Add("a", new InMemoryRedisCacheOptions { BatchTopicInvalidationThreshold = 1 }), peers.Add("b"));
        await a.Cache.SetAsync(Key, "old", null, Ct);
        var gate = new TaskCompletionSource();
        b.Inner.Hold = gate.Task;
        var read = b.Cache.GetAsync<string>(Key, null, Ct).AsTask();
        await Until(() => b.Inner.SingleReads == 1);

        await a.Cache.SetManyAsync<string>([new(Key, "new"), new("x", "y")], null, Ct);
        a.Published[^1].Type.Should().Be(KnownEventTypes.TopicInvalidated);
        peers.Store.WriteQuietly(Key, "old");
        gate.SetResult();
        (await read).Should().Be("old");

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("single")]
    [InlineData("keys")]
    [InlineData("many")]
    public async Task A_read_begun_while_this_node_s_removal_is_in_flight_does_not_bring_the_value_back(string how)
    {
        using var peers = new CachePeers();
        var (a, b) = await HoldingOld(peers);
        var gate = new TaskCompletionSource();
        b.Inner.RemoveHold = gate.Task;
        CacheKey[] keys = [Key, "k2"];
        Task removing = how switch
        {
            "single" => b.Cache.RemoveAsync<string>(Key, Ct).AsTask(),
            "keys" => b.Cache.RemoveAsync<string>(keys, Ct).AsTask(),
            _ => (Task)b.Cache.RemoveManyAsync<string>(keys, Ct).AsTask(),
        };
        b.Local.TryGetValue(Key, out _).Should().BeFalse();

        // The read reaches the store before the delete lands.
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().Be("old");
        gate.SetResult();
        await removing;

        b.Local.TryGetValue(Key, out _).Should().BeFalse();
        (await b.Cache.GetAsync<string>(Key, null, Ct)).Should().BeNull();
        b.Cache.BackgroundWritesActive.Should().BeFalse();
    }

    /// <summary>Node <c>a</c> wrote <c>old</c>, and node <c>b</c> read it into its local tier.</summary>
    private static Task<(CachePeers.Node A, CachePeers.Node B)> HoldingOld(CachePeers peers, InMemoryRedisCacheOptions? bOptions = null, RecordingTelemetryProvider? bTelemetry = null, CachePolicy? policy = null) =>
        HoldingOld(peers, [Key], bOptions, bTelemetry, policy);

    /// <summary>Node <c>a</c> wrote <c>old</c> to each key, and node <c>b</c> read them into its local tier.</summary>
    private static async Task<(CachePeers.Node A, CachePeers.Node B)> HoldingOld(CachePeers peers, string[] keys, InMemoryRedisCacheOptions? bOptions = null, RecordingTelemetryProvider? bTelemetry = null, CachePolicy? policy = null)
    {
        var a = peers.Add("a");
        var b = peers.Add("b", bOptions, bTelemetry);
        foreach (var key in keys)
        {
            await a.Cache.SetAsync(key, "old", null, Ct);
            (await b.Cache.GetAsync<string>(key, policy, Ct)).Should().Be("old");
            b.Local.TryGetValue(key, out _).Should().BeTrue();
        }

        return (a, b);
    }

    /// <summary>Node <c>a</c> writes <c>new</c>; its event is held back for the test to deliver.</summary>
    private static async Task<ICacheEvent> QuietSet(CachePeers peers, CachePeers.Node a)
    {
        peers.AutoDeliver = false;
        await a.Cache.SetAsync(Key, "new", null, Ct);
        return a.Published[^1];
    }

    private static string[] DroppedReasons(RecordingTelemetryProvider telemetry) =>
        [.. telemetry.Events.Where(e => e.Name == Dropped).Select(e => e.Properties!["reason"])];

    private static string[] DiscardedReasons(RecordingTelemetryProvider telemetry) =>
        [.. telemetry.Events.Where(e => e.Name == Discarded).Select(e => e.Properties!["reason"])];

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(5, Ct);
        }

        condition().Should().BeTrue();
    }

    /// <summary>Runs <paramref name="onEvent"/> as the dispatch reaches it, once.</summary>
    private sealed class StallingObserver(string key, Action onEvent) : IKeyedObserver<ICacheEvent>
    {
        private int _ran;

        public string Key => key;

        public void OnNext(ICacheEvent value)
        {
            if (Interlocked.Exchange(ref _ran, 1) == 0)
            {
                onEvent();
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
