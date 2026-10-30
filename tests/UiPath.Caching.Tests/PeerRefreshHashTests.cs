using Microsoft.Extensions.Caching.Memory;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>A peer's write to a hash this node holds whole, which it cannot lay over its copy, keeps the copy and replaces it with a full inner read in the background.</summary>
public class PeerRefreshHashTests
{
    private const string Key = "k";

    private static readonly CachePolicy Awaited = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30) };

    private static readonly HashCacheEntryOptions Merge = new(SetOption: HashCacheSetOption.HashReplace);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_peer_update_the_holder_could_not_apply_is_refreshed_with_a_full_read()
    {
        using var c = new PeerCluster();
        var (a, b) = (c.Add("a"), c.Add("b"));
        await Seed(a);
        (await b.Cache.GetAsync<string>(Key, Awaited, Ct)).Should().HaveCount(2);
        await b.Cache.GetAsync<string>(Key, Awaited, Ct);
        var before = b.InnerReads;
        var gate = new TaskCompletionSource();
        b.Cache.PeerRefreshHold = gate.Task;

        c.Redis.WriteQuietly(("z", "0"));
        await Add(a, ("c", "3"));

        Held(b).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2")));
        b.InnerReads.Should().Be(before);

        gate.SetResult();
        await b.Settled();

        Held(b).Should().BeEquivalentTo(c.Redis.Fields());
        (await b.Cache.GetAsync<string>(Key, Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("z", "0"), ("c", "3")));
        b.InnerReads.Should().Be(before + 1);
    }

    [Fact]
    public async Task A_peer_update_applied_in_place_keeps_the_read_time()
    {
        using var c = new PeerCluster();
        var telemetry = new Telemetry.RecordingTelemetryProvider();
        var (a, b) = (c.Add("a"), c.Add("b", telemetry: telemetry));
        await Seed(a);
        (await b.Cache.GetAsync<string>(Key, Awaited, Ct)).Should().HaveCount(2);
        var before = b.InnerReads;

        await Add(a, ("c", "3"));
        await b.Settled();
        b.InnerReads.Should().Be(before);

        c.Redis.WriteQuietly(("z", "0"));
        await Add(a, ("d", "4"));
        await b.Settled();

        Held(b).Should().BeEquivalentTo(c.Redis.Fields());
        b.InnerReads.Should().Be(before + 1);
        telemetry.Metrics.Where(m => m.Name == "cache.peer_refresh.dropped").Should().BeEmpty();
    }

    [Fact]
    public async Task A_hash_filled_by_a_read_is_refreshed_on_a_peer_update()
    {
        using var c = new PeerCluster();
        var (a, b) = (c.Add("a"), c.Add("b"));
        await Seed(a);
        (await b.Cache.GetAsync<string>(Key, Awaited, Ct)).Should().HaveCount(2);
        var before = b.InnerReads;

        c.Redis.WriteQuietly(("z", "0"));
        await Add(a, ("c", "3"));
        await b.Settled();

        Held(b).Should().NotBeNull();
        b.InnerReads.Should().Be(before + 1);
    }

    [Fact]
    public async Task A_refreshed_hash_carries_the_inner_version()
    {
        using var c = new PeerCluster();
        var (a, b) = (c.Add("a"), c.Add("b"));
        await Seed(a);
        await b.Cache.GetAsync<string>(Key, Awaited, Ct);
        await b.Cache.GetAsync<string>(Key, Awaited, Ct);
        c.Redis.WriteQuietly(("z", "0"));
        await Add(a, ("c", "3"));
        await b.Settled();
        var before = b.InnerReads;

        b.Local.TryGetValue(Key, out var refreshed).Should().BeTrue();
        HashVersions.NumberOf(refreshed!).Should().Be(c.Redis.Version());

        await Add(a, ("d", "4"));

        Held(b).Should().BeEquivalentTo(c.Redis.Fields());
        b.InnerReads.Should().Be(before);

        // The copy the update was laid over is refreshed again on the next gap, once it has been read.
        await b.Cache.GetAsync<string>(Key, Awaited, Ct);
        c.Redis.WriteQuietly(("y", "9"));
        await Add(a, ("e", "5"));
        await b.Settled();

        Held(b).Should().BeEquivalentTo(c.Redis.Fields());
        b.InnerReads.Should().Be(before + 1);
    }

    [Fact]
    public async Task A_field_update_after_a_pending_refresh_is_read_again_instead_of_applied()
    {
        using var c = new PeerCluster();
        var (a, b) = (c.Add("a"), c.Add("b"));
        await Seed(a);
        await b.Cache.GetAsync<string>(Key, Awaited, Ct);
        await b.Cache.GetAsync<string>(Key, Awaited, Ct);
        var before = b.InnerReads;
        c.AutoDeliver = false;
        await Add(a, ("c", "3"));
        await Add(a, ("d", "4"));
        var (next, after) = (a.Published[^2], a.Published[^1]);
        var gate = new TaskCompletionSource();
        b.Inner.ReadHold = gate.Task;

        // The later write first: two writes ahead of the copy, it cannot be applied, and the refresh starts reading.
        c.Deliver(b, after);
        await Until(() => b.InnerReads == before + 1);
        // One write ahead, it would apply, over a copy the refresh is about to replace.
        c.Deliver(b, next);

        Held(b).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2")));

        gate.SetResult();
        await b.Settled();

        Held(b).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("c", "3"), ("d", "4")));
        b.InnerReads.Should().Be(before + 2);
    }

    [Fact]
    public async Task A_read_begun_before_an_update_handed_to_the_refresh_does_not_install_the_old_hash()
    {
        using var c = new PeerCluster();
        var (a, b) = (c.Add("a"), c.Add("b"));
        var other = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(9), LocalExpirationDisconnected = TimeSpan.FromSeconds(30) };
        await Seed(a);
        var read = new TaskCompletionSource();
        b.Inner.HeldReadSnapshots = true;
        b.Inner.ReadHold = read.Task;
        // Its own run: a read under another policy does not join it.
        var early = b.Cache.GetAsync<string>(Key, Awaited, Ct).AsTask();
        await Until(() => b.InnerReads == 1);
        b.Inner.ReadHold = null;
        await b.Cache.GetAsync<string>(Key, other, Ct);
        await b.Cache.GetAsync<string>(Key, other, Ct);
        b.Local.TryGetValue(Key, out var held).Should().BeTrue();
        var refresh = new TaskCompletionSource();
        b.Cache.PeerRefreshHold = refresh.Task;

        c.Redis.WriteQuietly(("z", "0"));
        await Add(a, ("c", "3"));
        read.SetResult();
        (await early).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2")));

        b.Local.TryGetValue(Key, out var after).Should().BeTrue();
        after.Should().BeSameAs(held);

        refresh.SetResult();
        await b.Settled();

        Held(b).Should().BeEquivalentTo(c.Redis.Fields());
    }

    [Fact]
    public async Task A_background_merge_does_not_lay_its_fields_over_a_copy_awaiting_refresh()
    {
        using var c = new PeerCluster();
        var (a, b) = (c.Add("a"), c.Add("b"));
        var background = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30), BackgroundDistributedWrite = true };
        await Seed(a);
        await b.Cache.GetAsync<string>(Key, Awaited, Ct);
        await b.Cache.GetAsync<string>(Key, Awaited, Ct);
        var refresh = new TaskCompletionSource();
        b.Cache.PeerRefreshHold = refresh.Task;
        c.Redis.WriteQuietly(("z", "0"));
        await Add(a, ("c", "3"));

        (await b.Cache.SetAsync<string>(Key, Fields(("x", "9")), Merge, background, Ct)).Should().BeTrue();
        await Until(() => b.Cache.RunsInFlight == 1);
        refresh.SetResult();
        await b.Settled();

        (await b.Cache.GetAsync<string>(Key, Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        c.Redis.Fields().Should().ContainKeys("z", "c", "x");
    }

    [Fact]
    public async Task A_merge_between_the_update_falling_back_and_its_hand_over_does_not_lay_over_the_held_copy()
    {
        using var c = new PeerCluster();
        var (a, b) = (c.Add("a"), c.Add("b"));
        await Seed(a);
        await b.Cache.GetAsync<string>(Key, Awaited, Ct);
        c.AutoDeliver = false;
        c.Redis.WriteQuietly(("z", "0"));
        await Add(a, ("c", "3"));
        var update = a.Published[^1];
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        b.Cache.PeerRefreshHandOver = () =>
        {
            reached.TrySetResult();
            release.Task.Wait(Ct);
        };

        // The update could not be applied, and the dispatcher has let go of the merge lock but not yet queued the refresh.
        var delivering = Task.Run(() => c.Deliver(b, update), Ct);
        await reached.Task.WaitAsync(Ct);
        await Add(b, ("x", "9"));
        release.SetResult();
        await delivering;
        await b.Settled();

        (await b.Cache.GetAsync<string>(Key, Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        c.Redis.Fields().Should().ContainKeys("z", "c", "x");
    }

    [Fact]
    public async Task A_hand_over_that_throws_drops_the_held_copy()
    {
        using var c = new PeerCluster();
        var (a, b) = (c.Add("a"), c.Add("b"));
        await Seed(a);
        await b.Cache.GetAsync<string>(Key, Awaited, Ct);
        c.AutoDeliver = false;
        c.Redis.WriteQuietly(("z", "0"));
        await Add(a, ("c", "3"));
        var update = a.Published[^1];
        b.Cache.PeerRefreshHandOver = () => throw new InvalidOperationException("hand-over failed");

        c.Invoking(p => p.Deliver(b, update)).Should().NotThrow();

        Held(b).Should().BeNull();
        await b.Settled();
        (await b.Cache.GetAsync<string>(Key, Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
    }

    [Fact]
    public async Task A_hash_held_only_as_fields_is_dropped_not_refreshed()
    {
        using var c = new PeerCluster();
        var (a, b) = (c.Add("a"), c.Add("b", monitorsConnection: true));
        await Seed(a);
        b.Inner.IsConnected = false;
        (await b.Cache.SetAsync<string>(Key, Fields(("x", "9")), Merge, Awaited, Ct)).Should().BeTrue();
        b.Inner.IsConnected = true;
        Held(b).Should().BeEquivalentTo(Fields(("x", "9")));
        var before = b.InnerReads;

        await Add(a, ("c", "3"));

        Held(b).Should().BeNull();
        await b.Settled();
        b.InnerReads.Should().Be(before);
    }

    [Fact]
    public async Task A_hash_policy_that_opts_out_drops_as_before()
    {
        using var c = new PeerCluster();
        var (a, b) = (c.Add("a"), c.Add("b"));
        var optOut = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(10), RefreshLocalOnPeerWrite = false };
        await Seed(a);
        await b.Cache.GetAsync<string>(Key, optOut, Ct);
        var before = b.InnerReads;

        c.Redis.WriteQuietly(("z", "0"));
        await Add(a, ("c", "3"));

        Held(b).Should().BeNull();
        await b.Settled();
        b.InnerReads.Should().Be(before);
    }

    [Fact]
    public async Task A_peer_batch_of_hashes_is_refreshed_key_by_key()
    {
        using var c = new PeerCluster();
        var (a, b) = (c.Add("a", peerFieldUpdates: false), c.Add("b", peerFieldUpdates: false));
        await a.Cache.SetAsync<string>("k1", Fields(("a", "1")), Awaited, Ct);
        await a.Cache.SetAsync<string>("k2", Fields(("b", "2")), Awaited, Ct);
        await b.Cache.GetAsync<string>("k1", Awaited, Ct);
        await b.Cache.GetAsync<string>("k2", Awaited, Ct);
        await b.Cache.GetAsync<string>("k1", Awaited, Ct);
        await b.Cache.GetAsync<string>("k2", Awaited, Ct);
        var before = b.InnerReads;
        var gate = new TaskCompletionSource();
        b.Cache.PeerRefreshHold = gate.Task;

        await a.Cache.SetAsync<string>("k1", Fields(("a", "10")), Awaited, Ct);
        await a.Cache.SetAsync<string>("k2", Fields(("b", "20")), Awaited, Ct);

        Held(b, "k1").Should().BeEquivalentTo(Fields(("a", "1")));
        Held(b, "k2").Should().BeEquivalentTo(Fields(("b", "2")));

        gate.SetResult();
        await b.Settled();

        Held(b, "k1").Should().BeEquivalentTo(Fields(("a", "10")));
        Held(b, "k2").Should().BeEquivalentTo(Fields(("b", "20")));
        b.InnerReads.Should().Be(before + 2);
    }

    [Fact]
    public async Task A_read_begun_before_a_peer_s_write_of_a_hash_not_held_does_not_keep_the_old_hash()
    {
        using var c = new PeerCluster();
        var (a, b) = (c.Add("a"), c.Add("b"));
        await Seed(a);
        var read = new TaskCompletionSource();
        b.Inner.HeldReadSnapshots = true;
        b.Inner.ReadHold = read.Task;
        var early = b.Cache.GetAsync<string>(Key, Awaited, Ct).AsTask();
        await Until(() => b.InnerReads == 1);

        await Add(a, ("c", "3"));
        read.SetResult();
        (await early).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2")));

        Held(b).Should().BeNull();
        (await b.Cache.GetAsync<string>(Key, Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("c", "3")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_begun_while_this_node_s_field_removal_is_in_flight_does_not_bring_the_fields_back(bool peerFieldUpdates)
    {
        using var c = new PeerCluster();
        var (a, b) = (c.Add("a", peerFieldUpdates), c.Add("b", peerFieldUpdates));
        await Seed(a);
        (await b.Cache.GetAsync<string>(Key, Awaited, Ct)).Should().HaveCount(2);
        var gate = new TaskCompletionSource();
        b.Inner.RemoveHold = gate.Task;
        var removing = b.Cache.RemoveFieldsAsync<string>(Key, ["a"], Ct).AsTask();
        Held(b).Should().BeNull();

        (await b.Cache.GetAsync<string>(Key, Awaited, Ct)).Should().HaveCount(2);
        gate.SetResult();
        (await removing).Should().BeTrue();

        Held(b).Should().BeNull();
        (await b.Cache.GetAsync<string>(Key, Awaited, Ct)).Should().BeEquivalentTo(Fields(("b", "2")));
        b.Cache.BackgroundWritesActive.Should().Be(peerFieldUpdates);
    }

    private static IDictionary<string, string?>? Held(PeerCluster.Node node, string key = Key) =>
        node.Local.TryGetValue(key, out ICacheEntry<IDictionary<string, string?>>? held) ? held!.Value : null;

    private static Dictionary<string, string?> Fields(params (string Field, string Value)[] fields) =>
        fields.ToDictionary(f => f.Field, f => (string?)f.Value);

    private static async Task Seed(PeerCluster.Node node) =>
        (await node.Cache.SetAsync<string>(Key, Fields(("a", "1"), ("b", "2")), Awaited, Ct)).Should().BeTrue();

    private static async Task Add(PeerCluster.Node node, params (string Field, string Value)[] fields) =>
        (await node.Cache.SetAsync<string>(Key, Fields(fields), Merge, Awaited, Ct)).Should().BeTrue();

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(5, Ct);
        }

        condition().Should().BeTrue();
    }
}
