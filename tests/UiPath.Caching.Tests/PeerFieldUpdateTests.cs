#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>A node that holds a hash exactly one write behind a peer's small <see cref="HashCacheSetOption.HashReplace"/> adds the written fields to its copy; any other copy is dropped.</summary>
public class PeerFieldUpdateTests
{
    private static readonly CachePolicy Awaited = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30) };

    private static readonly CachePolicy Background = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30), BackgroundDistributedWrite = true };

    private static readonly CachePolicy AwaitedDropping = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30), RefreshLocalOnPeerWrite = false };

    private static readonly CachePolicy BackgroundDropping = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30), BackgroundDistributedWrite = true, RefreshLocalOnPeerWrite = false };

    private static readonly HashCacheEntryOptions Merge = new(SetOption: HashCacheSetOption.HashReplace);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_peer_one_write_behind_adds_the_written_fields_to_its_copy_and_keeps_it()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(2);

        await Add(writer, ("c", "3"));

        var seen = await peer.Cache.GetAsync<string>("k", Awaited, Ct);
        seen.Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("c", "3")));
        seen.Keys.Should().NotContain(KnownFieldNames.VersionKey);
        peer.InnerReads.Should().Be(1);
        c.Redis.Fields().Should().BeEquivalentTo(seen);
    }

    [Fact]
    public async Task A_dictionary_a_caller_holds_is_not_changed_by_an_update_applied_after_it_was_returned()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        var held = await peer.Cache.GetAsync<string>("k", Awaited, Ct);

        await Add(writer, ("c", "3"));

        held.Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2")));
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(3);
    }

    [Fact]
    public async Task A_peer_two_writes_behind_drops_its_copy()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct);
        c.Redis.WriteQuietly(("z", "0"));

        await Add(writer, ("c", "3"));

        (await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("z", "0"), ("c", "3")));
        peer.InnerReads.Should().Be(2);
    }

    [Fact]
    public async Task A_node_that_holds_nothing_does_nothing()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);

        await Add(writer, ("c", "3"));

        peer.InnerReads.Should().Be(0);
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(3);
        peer.InnerReads.Should().Be(1);
    }

    [Fact]
    public async Task A_peer_whose_copy_has_no_version_drops_it()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        c.Redis.ReadsVersions = false;
        await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct);
        c.Redis.ReadsVersions = true;

        await Add(writer, ("c", "3"));

        (await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().HaveCount(3);
        peer.InnerReads.Should().Be(2);
    }

    [Fact]
    public async Task A_late_event_after_newer_ones_were_applied_is_ignored_and_the_copy_kept()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", Awaited, Ct);
        await Add(writer, ("c", "3"));
        await Add(writer, ("d", "4"));
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(4);
        peer.InnerReads.Should().Be(1);

        c.Deliver(peer, writer.Published[1]);

        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(4);
        peer.InnerReads.Should().Be(1);
    }

    [Fact]
    public async Task An_event_delivered_twice_or_behind_a_read_that_already_counted_it_is_ignored_and_the_copy_kept()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        c.AutoDeliver = false;
        await Add(writer, ("c", "3"));
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(3);

        c.Deliver(peer, writer.Published[1]);
        c.Deliver(peer, writer.Published[1]);

        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("c", "3")));
        peer.InnerReads.Should().Be(1);
    }

    [Fact]
    public async Task A_late_event_of_an_older_epoch_of_the_hash_drops_the_copy_of_the_new_one()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await Add(writer, ("c", "3"));
        var old = writer.Published[^1];
        await writer.Cache.RemoveAsync<string>("k", Ct);
        await Seed(writer);
        await Add(writer, ("d", "4"));
        await Add(writer, ("e", "5"));
        await Add(writer, ("f", "6"));
        (await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().HaveCount(5);
        peer.InnerReads.Should().Be(1);

        c.Deliver(peer, old);

        (await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        peer.InnerReads.Should().Be(2);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(8, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(8, true)]
    public async Task A_lost_removal_followed_by_a_write_that_makes_the_hash_again_drops_the_copy_below_at_or_past_the_held_count(int writesAfterTheRemoval, bool newEpochAbove)
    {
        using var c = new PeerCluster();
        var draws = new Queue<long>([100L << HashVersion.CounterBits, (newEpochAbove ? 200L : 50L) << HashVersion.CounterBits]);
        c.Redis.NextEpoch = draws.Dequeue;
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        for (var i = 0; i < 4; i++)
        {
            await Add(writer, ($"m{i}", "1"));
        }

        (await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().HaveCount(6);
        (c.Redis.Version() & ((1L << HashVersion.CounterBits) - 1)).Should().Be(5);
        c.AutoDeliver = false;
        await writer.Cache.RemoveAsync<string>("k", Ct);
        await Add(writer, ("n0", "1"));
        for (var i = 1; i < writesAfterTheRemoval; i++)
        {
            await Add(writer, ($"n{i}", "1"));
        }

        // Only the last write event is delivered: the removal event and the others are lost.
        c.Deliver(peer, writer.Published[^1]);

        (await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        peer.InnerReads.Should().Be(2);
    }

    [Fact]
    public async Task A_lost_removal_with_no_write_after_it_leaves_the_copy_as_it_always_did()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await Add(writer, ("c", "3"));
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(3);

        c.Redis.Remove("k");

        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(3);
        peer.InnerReads.Should().Be(1);
    }

    [Fact]
    public async Task A_node_that_wrote_over_a_copy_whose_removal_it_never_heard_of_drops_what_it_holds_on_the_next_event()
    {
        using var c = new PeerCluster();
        var (node, other) = (c.Add("a"), c.Add("b"));
        await Seed(other, AwaitedDropping);
        await Add(other, AwaitedDropping, ("c", "3"));
        (await node.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().HaveCount(3);
        c.AutoDeliver = false;
        c.Redis.Remove("k");
        await Add(other, AwaitedDropping, ("n", "1"));
        c.AutoDeliver = true;

        await Add(node, AwaitedDropping, ("x", "1"));
        await Add(other, AwaitedDropping, ("y", "1"));

        (await node.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
    }

    [Fact]
    public async Task An_event_behind_a_copy_that_holds_writes_of_its_own_still_in_flight_is_ignored_and_the_writes_stay_over_the_copy()
    {
        using var c = new PeerCluster();
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await Add(other, ("d", "4"));
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(3);
        var release = new TaskCompletionSource();
        writer.Inner.Hold = release.Task;
        await writer.Cache.SetAsync<string>("k", Fields(("c", "3")), Merge, Background, Ct);
        var reads = writer.InnerReads;

        c.Deliver(writer, other.Published[^1]);

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("d", "4"), ("c", "3")));
        writer.InnerReads.Should().Be(reads);
        release.SetResult();
        await writer.Settled();
        await Add(other, ("e", "5"));
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        writer.InnerReads.Should().Be(reads);
    }

    [Fact]
    public async Task An_event_of_an_older_epoch_drops_a_copy_that_holds_writes_of_its_own_still_in_flight()
    {
        using var c = new PeerCluster();
        var (node, other) = (c.Add("a"), c.Add("b"));
        await Seed(other);
        await Add(other, ("c", "3"));
        var old = other.Published[^1];
        await other.Cache.RemoveAsync<string>("k", Ct);
        await Seed(other);
        await Add(other, ("d", "4"));
        (await node.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(3);
        var release = new TaskCompletionSource();
        node.Inner.Hold = release.Task;
        await node.Cache.SetAsync<string>("k", Fields(("e", "5")), Merge, Background, Ct);
        var reads = node.InnerReads;

        c.Deliver(node, old);

        await node.Cache.GetAsync<string>("k", Awaited, Ct);
        node.InnerReads.Should().Be(reads + 1);
        release.SetResult();
        await node.Settled();
    }

    [Fact]
    public async Task An_event_of_the_node_s_own_write_delivered_again_leaves_its_copy_alone()
    {
        using var c = new PeerCluster();
        var node = c.Add("a");
        await Seed(node);
        await Add(node, ("c", "3"));
        await Add(node, ("d", "4"));
        (await node.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(4);
        var reads = node.InnerReads;

        c.Deliver(node, node.Published[1]);

        (await node.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        node.InnerReads.Should().Be(reads);
    }

    [Fact]
    public async Task A_count_with_no_epoch_from_a_node_that_draws_none_is_never_ignored_as_late()
    {
        using var c = new PeerCluster();
        c.Redis.Epochs = false;
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct);
        await Add(writer, ("c", "3"));
        await Add(writer, ("d", "4"));
        (await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().HaveCount(4);
        peer.InnerReads.Should().Be(1);

        c.Deliver(peer, writer.Published[1]);

        (await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().HaveCount(4);
        peer.InnerReads.Should().Be(2);
    }

    [Fact]
    public async Task A_count_with_no_epoch_from_a_node_that_draws_none_is_never_ignored_as_late_and_the_copy_is_refreshed()
    {
        using var c = new PeerCluster();
        c.Redis.Epochs = false;
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", Awaited, Ct);
        await Add(writer, ("c", "3"));
        await Add(writer, ("d", "4"));
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(4);
        peer.InnerReads.Should().Be(1);

        c.Deliver(peer, writer.Published[1]);
        await peer.Settled();

        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(4);
        peer.InnerReads.Should().Be(2);
    }

    [Fact]
    public async Task A_loss_of_events_clears_a_copy_that_ignored_a_late_event()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", Awaited, Ct);
        await Add(writer, ("c", "3"));
        c.Deliver(peer, writer.Published[1]);
        peer.InnerReads.Should().Be(1);

        peer.Subject.Invalidate(MissedEventsReason.Lost);

        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(3);
        peer.InnerReads.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_awaited_merge_that_a_background_merge_of_the_key_overtook_leaves_no_copy_that_lacks_its_fields_or_waits_for_its_version(bool peerWritesBetween)
    {
        using var c = new PeerCluster();
        var (node, other) = (c.Add("a"), c.Add("b"));
        await Seed(node);
        (await node.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(2);
        var release = new TaskCompletionSource();
        node.Inner.Hold = release.Task;
        var awaited = node.Cache.SetAsync<string>("k", Fields(("c", "3")), Merge, Awaited, Ct).AsTask();
        await node.Cache.SetAsync<string>("k", Fields(("d", "4")), Merge, Background, Ct);

        release.SetResult();
        (await awaited).Should().BeTrue();
        await node.Settled();
        if (peerWritesBetween)
        {
            await Add(other, ("e", "5"));
        }

        (await node.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        await Add(other, ("f", "6"));
        (await node.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
    }

    [Fact]
    public async Task Two_awaited_merges_that_return_out_of_the_order_the_hash_took_them_leave_the_value_of_the_later_one()
    {
        using var c = new PeerCluster();
        var node = c.Add("a");
        await Seed(node);
        (await node.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(2);
        var first = new TaskCompletionSource();
        var firstWritten = new TaskCompletionSource();
        node.Inner.AfterWrite = values =>
        {
            if (values.TryGetValue("x", out var v) && v == "first")
            {
                firstWritten.TrySetResult();
                return first.Task;
            }

            return Task.CompletedTask;
        };
        var early = node.Cache.SetAsync<string>("k", Fields(("x", "first")), Merge, Awaited, Ct).AsTask();
        await firstWritten.Task;
        await node.Cache.SetAsync<string>("k", Fields(("x", "second")), Merge, Awaited, Ct);

        first.SetResult();
        (await early).Should().BeTrue();

        c.Redis.Fields()!["x"].Should().Be("second");
        (await node.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
    }

    [Fact]
    public async Task A_replacement_that_returns_after_a_merge_made_to_the_hash_it_replaced_does_not_keep_a_copy_without_that_merge()
    {
        using var c = new PeerCluster();
        var node = c.Add("a");
        await Seed(node);
        var replaced = new TaskCompletionSource();
        var replaceWritten = new TaskCompletionSource();
        node.Inner.AfterWrite = values =>
        {
            if (values.ContainsKey("r"))
            {
                replaceWritten.TrySetResult();
                return replaced.Task;
            }

            return Task.CompletedTask;
        };
        var replace = node.Cache.SetAsync<string>("k", Fields(("r", "1")), new HashCacheEntryOptions(SetOption: HashCacheSetOption.KeyReplace), Awaited, Ct).AsTask();
        await replaceWritten.Task;
        await node.Cache.SetAsync<string>("k", Fields(("m", "2")), Merge, Awaited, Ct);

        replaced.SetResult();
        (await replace).Should().BeTrue();

        c.Redis.Fields().Should().BeEquivalentTo(Fields(("r", "1"), ("m", "2")));
        (await node.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
    }

    [Fact]
    public async Task A_write_that_returns_after_a_read_already_counted_it_does_not_put_its_values_over_the_read()
    {
        using var c = new PeerCluster();
        var (node, other) = (c.Add("a"), c.Add("b"));
        await Seed(node, AwaitedDropping);
        var held = new TaskCompletionSource();
        var written = new TaskCompletionSource();
        node.Inner.AfterWrite = values =>
        {
            if (values.TryGetValue("x", out var v) && v == "old")
            {
                written.TrySetResult();
                return held.Task;
            }

            return Task.CompletedTask;
        };
        await node.Cache.GetAsync<string>("k", AwaitedDropping, Ct);
        var slow = node.Cache.SetAsync<string>("k", Fields(("x", "old")), Merge, AwaitedDropping, Ct).AsTask();
        await written.Task;
        await Add(other, AwaitedDropping, ("x", "new"));
        (await node.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().ContainKey("x");

        held.SetResult();
        (await slow).Should().BeTrue();

        c.Redis.Fields()!["x"].Should().Be("new");
        (await node.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
    }

    [Fact]
    public async Task A_write_that_returns_after_a_read_already_counted_it_does_not_put_its_values_over_the_refreshed_copy()
    {
        using var c = new PeerCluster();
        var (node, other) = (c.Add("a"), c.Add("b"));
        await Seed(node, Awaited);
        var held = new TaskCompletionSource();
        var written = new TaskCompletionSource();
        node.Inner.AfterWrite = values =>
        {
            if (values.TryGetValue("x", out var v) && v == "old")
            {
                written.TrySetResult();
                return held.Task;
            }

            return Task.CompletedTask;
        };
        await node.Cache.GetAsync<string>("k", Awaited, Ct);
        var slow = node.Cache.SetAsync<string>("k", Fields(("x", "old")), Merge, Awaited, Ct).AsTask();
        await written.Task;
        await Add(other, Awaited, ("x", "new"));

        await node.Settled();
        held.SetResult();
        (await slow).Should().BeTrue();
        await node.Settled();

        c.Redis.Fields()!["x"].Should().Be("new");
        (await node.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
    }

    [Fact]
    public async Task A_write_that_returns_after_a_peer_s_later_write_does_not_lay_its_values_over_the_copy_awaiting_refresh()
    {
        using var c = new PeerCluster();
        var telemetry = new Telemetry.RecordingTelemetryProvider();
        var (node, other) = (c.Add("a", telemetry: telemetry), c.Add("b"));
        await Seed(node, Awaited);
        var held = new TaskCompletionSource();
        var written = new TaskCompletionSource();
        node.Inner.AfterWrite = values =>
        {
            if (values.TryGetValue("x", out var v) && v == "old")
            {
                written.TrySetResult();
                return held.Task;
            }

            return Task.CompletedTask;
        };
        await node.Cache.GetAsync<string>("k", Awaited, Ct);
        var refresh = new TaskCompletionSource();
        node.Cache.PeerRefreshHold = refresh.Task;
        var slow = node.Cache.SetAsync<string>("k", Fields(("x", "old")), Merge, Awaited, Ct).AsTask();
        await written.Task;
        await Add(other, Awaited, ("x", "new"));

        // The node's own write returns before the refresh of the peer's later one runs.
        held.SetResult();
        (await slow).Should().BeTrue();
        refresh.SetResult();
        await node.Settled();

        c.Redis.Fields()!["x"].Should().Be("new");
        (await node.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        telemetry.Events.Where(e => e.Name.StartsWith("cache.peer_refresh.", StringComparison.Ordinal) && e.Name != "cache.peer_refresh.started")
            .Select(e => e.Name + (e.Properties!.TryGetValue("reason", out var reason) ? ":" + reason : string.Empty))
            .Should().Equal("cache.peer_refresh.discarded:newer_local");
    }

    [Fact]
    public async Task A_replacement_of_the_whole_key_drops_the_peer_s_copy()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct);

        await writer.Cache.SetAsync<string>("k", Fields(("c", "3")), new HashCacheEntryOptions(SetOption: HashCacheSetOption.KeyReplace), AwaitedDropping, Ct);

        (await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().BeEquivalentTo(Fields(("c", "3")));
        peer.InnerReads.Should().Be(2);
        writer.Published[^1].Data!.Properties.Should().BeNull();
    }

    [Fact]
    public async Task Removing_the_key_drops_the_peer_s_copy()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", Awaited, Ct);

        (await writer.Cache.RemoveAsync<string>("k", Ct)).Should().BeTrue();
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEmpty();
        peer.InnerReads.Should().Be(2);
        writer.Published.Skip(1).Should().OnlyContain(e => e.Data!.Properties == null);
    }

    [Fact]
    public async Task Removing_fields_counts_a_write_and_drops_the_writer_s_and_the_peer_s_copies()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", Awaited, Ct);
        var before = c.Redis.Version();

        (await writer.Cache.RemoveFieldsAsync<string>("k", ["a"], Ct)).Should().BeTrue();

        writer.Inner.CountedRemovals.Should().Be(1);
        c.Redis.Version().Should().Be(before + 1);
        writer.Published[^1].Type.Should().Be(KnownEventTypes.CacheRemoved);
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("b", "2")));
        peer.InnerReads.Should().Be(2);
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("b", "2")));
        writer.InnerReads.Should().Be(1);
    }

    [Fact]
    public async Task Copies_read_after_a_field_removal_are_at_its_count_and_take_the_next_write_on_both_nodes()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await writer.Cache.RemoveFieldsAsync<string>("k", ["a"], Ct);
        await writer.Cache.GetAsync<string>("k", Awaited, Ct);
        await peer.Cache.GetAsync<string>("k", Awaited, Ct);

        await Add(writer, ("c", "3"));
        await Add(peer, ("d", "4"));

        var expected = Fields(("b", "2"), ("c", "3"), ("d", "4"));
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(expected);
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(expected);
        peer.InnerReads.Should().Be(1);
        writer.InnerReads.Should().Be(1);
        c.Redis.Fields().Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Removing_the_last_fields_removes_the_hash_and_its_count_and_both_nodes_read_it_empty()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", Awaited, Ct);

        (await writer.Cache.RemoveFieldsAsync<string>("k", ["a", "b"], Ct)).Should().BeTrue();

        c.Redis.Fields().Should().BeNull();
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEmpty();
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_peer_whose_decode_of_the_carried_fields_fails_drops_its_copy()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct);
        peer.Inner.FailsDecode = true;

        await Add(writer, ("c", "3"));

        writer.Published[^1].Data!.Properties.Should().ContainKey(KnownFieldNames.PeerFieldsKey);
        (await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("c", "3")));
        peer.InnerReads.Should().Be(2);
    }

    [Fact]
    public async Task A_fill_reported_unwritten_keeps_its_copy_without_the_version_it_reported()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        writer.Inner.ReportsUnwritten = true;

        (await writer.Cache.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(Fields(("a", "1"))), AwaitedDropping, Ct)).Should().HaveCount(1);
        writer.Inner.ReportsUnwritten = false;
        (await writer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().HaveCount(1);
        writer.InnerReads.Should().Be(1);

        // Stamped with the reported count, the copy would be one write behind the peer's and take it.
        await Add(peer, ("b", "2"));

        (await writer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2")));
        writer.InnerReads.Should().Be(2);
    }

    [Fact]
    public async Task A_fill_reported_unwritten_has_its_copy_refreshed_instead_of_merged()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        writer.Inner.ReportsUnwritten = true;

        (await writer.Cache.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(Fields(("a", "1"))), Awaited, Ct)).Should().HaveCount(1);
        writer.Inner.ReportsUnwritten = false;
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(1);
        writer.InnerReads.Should().Be(1);

        // Stamped with the reported count, the copy would be one write behind the peer's and take it.
        await Add(peer, ("b", "2"));
        await writer.Settled();

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2")));
        writer.InnerReads.Should().Be(2);
    }

    [Fact]
    public async Task A_write_over_the_field_limit_or_the_byte_limit_carries_no_fields_and_the_peer_drops()
    {
        using var c = new PeerCluster();
        var narrow = c.Add("a", maxFields: 2);
        var small = c.Add("b", maxBytes: 8);
        var peer = c.Add("p");
        await Seed(narrow);
        await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct);

        await Add(narrow, ("c", "3"), ("d", "4"), ("e", "5"));

        narrow.Published[^1].Data!.Properties.Should().BeNull();
        (await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().HaveCount(5);
        peer.InnerReads.Should().Be(2);

        await Add(small, ("f", "a value longer than eight bytes"));

        small.Published[^1].Data!.Properties.Should().BeNull();
        (await peer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().HaveCount(6);
        peer.InnerReads.Should().Be(3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task A_field_limit_below_one_is_taken_as_one_and_does_not_switch_carrying_off(int maxFields)
    {
        using var c = new PeerCluster();
        var writer = c.Add("a", maxFields: maxFields);
        await Seed(writer);

        await Add(writer, ("c", "3"));

        writer.Published[^1].Data!.Properties.Should().ContainKey(KnownFieldNames.PeerFieldsKey);
    }

    [Fact]
    public async Task A_small_merge_carries_its_version_and_fields_and_not_the_refresh_properties_an_older_node_would_act_on()
    {
        using var c = new PeerCluster();
        var writer = c.Add("a");
        await Seed(writer);

        await writer.Cache.SetAsync<string>("k", Fields(("c", "3")), new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(20), Metadata: new Dictionary<string, string?> { ["m"] = "1" }, SetOption: HashCacheSetOption.HashReplace), Awaited, Ct);

        var properties = writer.Published[^1].Data!.Properties!;
        ((long)properties[KnownFieldNames.VersionKey]! & ((1L << HashVersion.CounterBits) - 1)).Should().Be(2);
        ((long)properties[KnownFieldNames.VersionKey]! >> HashVersion.CounterBits).Should().BePositive();
        properties[KnownFieldNames.PeerFieldsKey].Should().BeAssignableTo<IDictionary<string, string?>>().Which.Keys.Should().BeEquivalentTo("c");
        properties.Keys.Should().NotContain([KnownFieldNames.ExpirationKey, KnownFieldNames.MetadataKey]);
    }

    [Fact]
    public async Task A_node_with_the_option_off_counts_nothing_and_its_events_carry_no_fields()
    {
        using var c = new PeerCluster();
        var writer = c.Add("a", peerFieldUpdates: false);
        await Seed(writer);

        await Add(writer, ("c", "3"));

        writer.Inner.CountedWrites.Should().Be(0);
        writer.Inner.PlainWrites.Should().Be(2);
        c.Redis.CountedWrites.Should().Be(0);
        writer.Published.Should().OnlyContain(e => e.Data!.Properties == null);
    }

    [Fact]
    public async Task A_peer_with_the_option_off_drops_on_an_event_that_carries_fields()
    {
        using var c = new PeerCluster();
        var writer = c.Add("a");
        var older = c.Add("b", peerFieldUpdates: false);
        await Seed(writer);
        await older.Cache.GetAsync<string>("k", AwaitedDropping, Ct);

        await Add(writer, ("c", "3"));

        (await older.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().HaveCount(3);
        older.InnerReads.Should().Be(2);
    }

    [Fact]
    public async Task A_peer_with_the_option_off_refreshes_on_an_event_that_carries_fields()
    {
        using var c = new PeerCluster();
        var writer = c.Add("a");
        var older = c.Add("b", peerFieldUpdates: false);
        await Seed(writer);
        await older.Cache.GetAsync<string>("k", Awaited, Ct);

        await Add(writer, ("c", "3"));
        await older.Settled();

        (await older.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(3);
        older.InnerReads.Should().Be(2);
    }

    [Fact]
    public async Task The_writer_s_own_copy_is_at_the_new_version_and_takes_a_peer_s_next_write()
    {
        using var c = new PeerCluster();
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await Add(writer, ("c", "3"));

        await Add(other, ("d", "4"));

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("c", "3"), ("d", "4")));
        writer.InnerReads.Should().Be(0);
    }

    [Fact]
    public async Task A_writer_whose_copy_was_two_writes_behind_keeps_its_merge_unversioned_and_drops_it_on_the_next_event()
    {
        using var c = new PeerCluster();
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer, AwaitedDropping);
        c.Redis.WriteQuietly(("z", "0"));
        await Add(writer, AwaitedDropping, ("c", "3"));

        await Add(other, AwaitedDropping, ("d", "4"));

        (await writer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("z", "0"), ("c", "3"), ("d", "4")));
        writer.InnerReads.Should().Be(1);
    }

    [Fact]
    public async Task A_background_merge_gives_the_writer_s_copy_the_new_version_once_it_is_written()
    {
        using var c = new PeerCluster();
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await writer.Cache.SetAsync<string>("k", Fields(("c", "3")), Merge, Background, Ct);
        await writer.Settled();

        await Add(other, ("d", "4"));

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("c", "3"), ("d", "4")));
        writer.InnerReads.Should().Be(0);
        writer.Published[^1].Data!.Properties.Should().ContainKey(KnownFieldNames.PeerFieldsKey);
    }

    [Fact]
    public async Task A_background_merge_over_a_copy_two_writes_behind_leaves_the_copy_unversioned()
    {
        using var c = new PeerCluster();
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        c.Redis.WriteQuietly(("z", "0"));
        await writer.Cache.SetAsync<string>("k", Fields(("c", "3")), Merge, BackgroundDropping, Ct);
        await writer.Settled();

        await Add(other, ("d", "4"));

        (await writer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("z", "0"), ("c", "3"), ("d", "4")));
        writer.InnerReads.Should().Be(1);
    }

    [Fact]
    public async Task A_background_merge_in_flight_takes_a_peer_s_next_write_and_the_version_of_its_own_once_written()
    {
        using var c = new PeerCluster();
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        var release = new TaskCompletionSource();
        writer.Inner.Hold = release.Task;
        await writer.Cache.SetAsync<string>("k", Fields(("c", "3")), Merge, Background, Ct);

        await Add(other, ("d", "4"));
        release.SetResult();
        await writer.Settled();
        await Add(other, ("e", "5"));

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        writer.InnerReads.Should().Be(0);
    }

    [Fact]
    public async Task A_background_merge_over_a_merge_still_in_flight_takes_the_version_once_both_are_written()
    {
        using var c = new PeerCluster();
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        var release = new TaskCompletionSource();
        writer.Inner.Hold = release.Task;
        await writer.Cache.SetAsync<string>("k", Fields(("c", "3")), Merge, Background, Ct);
        await writer.Cache.SetAsync<string>("k", Fields(("d", "4")), Merge, Background, Ct);
        release.SetResult();
        await writer.Settled();

        await Add(other, ("e", "5"));

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("c", "3"), ("d", "4"), ("e", "5")));
        writer.InnerReads.Should().Be(0);
    }

    [Fact]
    public async Task A_background_merge_over_a_background_fill_still_in_flight_takes_the_version_once_both_are_written()
    {
        using var c = new PeerCluster();
        var (writer, other) = (c.Add("a"), c.Add("b"));
        var release = new TaskCompletionSource();
        writer.Inner.Hold = release.Task;
        await writer.Cache.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(Fields(("a", "1"))), Background, Ct);
        await writer.Cache.SetAsync<string>("k", Fields(("c", "3")), Merge, Background, Ct);
        release.SetResult();
        await writer.Settled();
        var reads = writer.InnerReads;

        await Add(other, ("d", "4"));

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("c", "3"), ("d", "4")));
        writer.InnerReads.Should().Be(reads);
    }

    [Fact]
    public async Task An_update_that_arrives_while_the_fill_that_made_the_copy_is_in_flight_drops_the_copy()
    {
        using var c = new PeerCluster();
        var (writer, other) = (c.Add("a"), c.Add("b"));
        var release = new TaskCompletionSource();
        writer.Inner.Hold = release.Task;
        await writer.Cache.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(Fields(("a", "1"))), Background, Ct);
        await Add(other, ("z", "0"));
        release.SetResult();
        await writer.Settled();
        var reads = writer.InnerReads;

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        writer.InnerReads.Should().Be(reads + 1);
    }

    [Fact]
    public async Task A_merge_two_writes_past_its_copy_takes_the_missing_write_when_its_event_comes_and_keeps_the_copy()
    {
        using var c = new PeerCluster { AutoDeliver = false };
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await Add(other, ("z", "0"));
        await Add(writer, ("c", "3"));

        c.Deliver(writer, other.Published[^1]);

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("z", "0"), ("c", "3")));
        writer.InnerReads.Should().Be(0);
        c.AutoDeliver = true;
        await Add(other, ("d", "4"));
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        writer.InnerReads.Should().Be(0);
    }

    [Fact]
    public async Task A_background_merge_two_writes_past_its_copy_takes_the_missing_write_when_its_event_comes_and_keeps_the_copy()
    {
        using var c = new PeerCluster { AutoDeliver = false };
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await Add(other, ("z", "0"));
        await writer.Cache.SetAsync<string>("k", Fields(("c", "3")), Merge, Background, Ct);
        await writer.Settled();

        c.Deliver(writer, other.Published[^1]);

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("z", "0"), ("c", "3")));
        writer.InnerReads.Should().Be(0);
    }

    [Fact]
    public async Task A_missing_write_that_came_before_the_node_s_own_on_a_field_does_not_replace_the_node_s_value()
    {
        using var c = new PeerCluster { AutoDeliver = false };
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await Add(other, ("c", "theirs"));
        await Add(writer, ("c", "mine"));

        c.Deliver(writer, other.Published[^1]);

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("c", "mine")));
        c.Redis.Fields().Should().ContainKey("c").WhoseValue.Should().Be("mine");
        writer.InnerReads.Should().Be(0);
    }

    [Fact]
    public async Task A_merge_replaces_a_field_it_changes_keeps_one_it_rewrites_unchanged_and_adds_one_it_lacks_on_the_writer_and_the_peer()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await writer.Cache.GetAsync<string>("k", Awaited, Ct);
        await peer.Cache.GetAsync<string>("k", Awaited, Ct);

        await Add(writer, ("a", "changed"), ("b", "2"), ("c", "3"));

        var expected = Fields(("a", "changed"), ("b", "2"), ("c", "3"));
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(expected);
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(expected);
        (writer.InnerReads, peer.InnerReads).Should().Be((0, 1));
    }

    [Fact]
    public async Task A_merge_that_rewrites_only_values_the_copy_already_holds_keeps_the_copy_and_its_fields()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await writer.Cache.GetAsync<string>("k", Awaited, Ct);
        await peer.Cache.GetAsync<string>("k", Awaited, Ct);

        await Add(writer, ("a", "1"));
        await Add(writer, ("b", "2"));

        var expected = Fields(("a", "1"), ("b", "2"));
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(expected);
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(expected);
        (writer.InnerReads, peer.InnerReads).Should().Be((0, 1));
    }

    [Fact]
    public async Task A_missing_write_takes_the_fields_the_node_has_not_written_since_and_leaves_the_ones_it_has()
    {
        using var c = new PeerCluster { AutoDeliver = false };
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await Add(other, ("a", "theirs"), ("c", "theirs"), ("d", "theirs"));
        await Add(writer, ("c", "mine"));

        c.Deliver(writer, other.Published[^1]);

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "theirs"), ("b", "2"), ("c", "mine"), ("d", "theirs")));
        writer.InnerReads.Should().Be(0);
    }

    [Fact]
    public async Task Merging_one_field_into_a_large_copy_does_not_copy_the_copy()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        var hash = Enumerable.Range(0, 200).ToDictionary(i => $"f{i}", i => (string?)$"v{i}");
        (await writer.Cache.SetAsync<string>("k", hash, Awaited, Ct)).Should().BeTrue();
        await writer.Cache.GetAsync<string>("k", Awaited, Ct);
        await peer.Cache.GetAsync<string>("k", Awaited, Ct);
        for (var i = 0; i < 50; i++)
        {
            await Add(writer, ("f0", $"w{i}"));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            await Add(writer, ("f0", $"x{i}"));
        }

        var perMerge = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        peer.InnerReads.Should().Be(1);
        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().ContainKey("f0").WhoseValue.Should().Be("x99");
        perMerge.Should().BeLessThan(25_000);
    }

    [Fact]
    public async Task Missing_writes_take_their_turns_in_order_and_the_copy_ends_at_the_hash()
    {
        using var c = new PeerCluster { AutoDeliver = false };
        var (writer, other, third) = (c.Add("a"), c.Add("b"), c.Add("c"));
        await Seed(writer);
        await Add(other, ("y", "1"));
        await Add(third, ("z", "2"));
        await Add(writer, ("c", "3"));

        c.Deliver(writer, other.Published[^1]);
        c.Deliver(writer, third.Published[^1]);

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        writer.InnerReads.Should().Be(0);
    }

    [Fact]
    public async Task A_missing_write_whose_event_comes_after_a_later_missing_one_drops_the_copy()
    {
        using var c = new PeerCluster { AutoDeliver = false };
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer, AwaitedDropping);
        await Add(other, AwaitedDropping, ("y", "1"));
        await Add(other, AwaitedDropping, ("z", "2"));
        await Add(writer, AwaitedDropping, ("c", "3"));

        c.Deliver(writer, other.Published[^1]);

        (await writer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        writer.InnerReads.Should().Be(1);
    }

    [Fact]
    public async Task A_background_merge_the_inner_tier_refused_leaves_a_copy_that_no_peer_write_is_applied_to()
    {
        using var c = new PeerCluster();
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        writer.Inner.Refuses = true;
        await writer.Cache.SetAsync<string>("k", Fields(("c", "3")), Merge, Background, Ct);
        await writer.Settled();

        await Add(other, ("d", "4"));

        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2"), ("d", "4")));
        writer.InnerReads.Should().Be(1);
    }

    [Theory]
    [InlineData(32, 0)]
    [InlineData(33, 1)]
    public async Task A_copy_holds_at_most_32_writes_of_its_own_ahead_of_the_hash_before_it_has_no_version(int merges, int reads)
    {
        using var c = new PeerCluster();
        var (writer, other) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        var release = new TaskCompletionSource();
        writer.Inner.Hold = release.Task;
        for (var i = 0; i < merges; i++)
        {
            await writer.Cache.SetAsync<string>("k", Fields(($"f{i}", "x")), Merge, BackgroundDropping, Ct);
        }

        release.SetResult();
        await writer.Settled();

        await Add(other, ("d", "4"));

        (await writer.Cache.GetAsync<string>("k", AwaitedDropping, Ct)).Should().BeEquivalentTo(c.Redis.Fields());
        writer.InnerReads.Should().Be(reads);
    }

    [Fact]
    public async Task The_applied_copy_takes_the_write_s_expiration_and_keeps_or_replaces_the_metadata()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", Awaited, Ct);
        var metadata = new Dictionary<string, string?> { ["m"] = "1" };

        await writer.Cache.SetAsync<string>("k", Fields(("c", "3")), new HashCacheEntryOptions(Metadata: metadata, SetOption: HashCacheSetOption.HashReplace), Awaited, Ct);
        (await peer.Cache.GetCacheEntryAsync<string>("k", Awaited, Ct)).Metadata.Should().BeEquivalentTo(metadata);
        await writer.Cache.SetAsync<string>("k", Fields(("d", "4")), new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(20), SetOption: HashCacheSetOption.HashReplace), Awaited, Ct);

        var entry = await peer.Cache.GetCacheEntryAsync<string>("k", Awaited, Ct);
        entry.Value.Should().HaveCount(4);
        entry.Metadata.Should().BeEquivalentTo(metadata);
        entry.Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(20), TimeSpan.FromMinutes(1));
        entry.Expiration.Should().BeCloseTo(c.Redis.Expiration(), TimeSpan.FromSeconds(1));
        peer.InnerReads.Should().Be(1);
    }

    [Fact]
    public async Task A_refresh_event_keeps_the_copy_s_version_so_a_later_write_still_applies()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", Awaited, Ct);
        (await writer.Cache.SetMetadataAsync<string>("k", new Dictionary<string, string?> { ["m"] = "1" }, Ct)).Should().BeTrue();

        await Add(writer, ("c", "3"));

        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(3);
        peer.InnerReads.Should().Be(1);
    }

    [Fact]
    public async Task An_applied_copy_does_not_outlive_the_local_cap_it_was_read_under()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        var capped = new CachePolicy { LocalExpiration = TimeSpan.FromMilliseconds(800), LocalExpirationDisconnected = TimeSpan.FromSeconds(30) };
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", capped, Ct);
        await Task.Delay(400, Ct);
        await Add(writer, ("c", "3"));
        (await peer.Cache.GetAsync<string>("k", capped, Ct)).Should().HaveCount(3);
        peer.InnerReads.Should().Be(1);

        await Task.Delay(700, Ct);

        (await peer.Cache.GetAsync<string>("k", capped, Ct)).Should().HaveCount(3);
        peer.InnerReads.Should().Be(2);
    }

    [Fact]
    public async Task A_loss_of_events_still_clears_an_applied_copy()
    {
        using var c = new PeerCluster();
        var (writer, peer) = (c.Add("a"), c.Add("b"));
        await Seed(writer);
        await peer.Cache.GetAsync<string>("k", Awaited, Ct);
        await Add(writer, ("c", "3"));

        peer.Subject.Invalidate(MissedEventsReason.Lost);

        (await peer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(3);
        peer.InnerReads.Should().Be(2);
    }

    [Fact]
    public async Task A_local_merge_and_a_peer_apply_on_one_key_never_leave_a_field_missing_from_a_kept_copy()
    {
        using var c = new PeerCluster();
        var (local, remote) = (c.Add("a"), c.Add("b"));
        await Seed(local);
        for (var i = 0; i < 300; i++)
        {
            await local.Cache.GetAsync<string>("k", Awaited, Ct);
            using var barrier = new Barrier(2);
            var index = i;
            await Task.WhenAll(
                Task.Run(() => Race(barrier, local, $"l{index}"), Ct),
                Task.Run(() => Race(barrier, remote, $"r{index}"), Ct));

            (await local.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(c.Redis.Fields(), $"iteration {i}");
        }
    }

    [Fact]
    public void A_token_hands_a_set_event_s_fields_to_its_handler_and_stays_unchanged_when_the_handler_applies_them()
    {
        var (subject, token) = Token();
        using var _ = token;
        var seen = new List<PeerUpdate>();
        ((IPeerUpdateSource)token).PeerUpdateHandler = update =>
        {
            seen.Add(update);
            return true;
        };

        subject.OnNext(SetEvent("1", new Dictionary<string, object?> { [KnownFieldNames.VersionKey] = 7L, [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, string?> { ["f"] = "dg==" }, ["_unknown_"] = "ignored" }));

        token.HasChanged.Should().BeFalse();
        seen.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Version = 7L, Fields = new Dictionary<string, string?> { ["f"] = "dg==" } });
    }

    [Fact]
    public void A_token_changes_without_an_expiration_or_metadata_when_its_handler_declines_or_there_is_none()
    {
        var (subject, token) = Token();
        using var _ = token;
        var properties = new Dictionary<string, object?> { [KnownFieldNames.VersionKey] = 7L, [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, string?> { ["f"] = "dg==" } };

        subject.OnNext(SetEvent("1", properties));

        token.HasChanged.Should().BeTrue();
        token.Expiration.Should().BeNull();
        token.MetadataHasChanged.Should().BeFalse();
        var (second, declined) = Token();
        using var __ = declined;
        ((IPeerUpdateSource)declined).PeerUpdateHandler = _ => false;

        second.OnNext(SetEvent("2", properties));

        declined.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void A_token_never_offers_a_removal_or_an_incomplete_update_to_its_handler()
    {
        var (subject, token) = Token();
        using var _ = token;
        var offered = 0;
        ((IPeerUpdateSource)token).PeerUpdateHandler = _ =>
        {
            offered++;
            return true;
        };

        subject.OnNext(SetEvent("1", new Dictionary<string, object?> { [KnownFieldNames.VersionKey] = 7L }));
        subject.OnNext(SetEvent("2", new Dictionary<string, object?> { [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, string?> { ["f"] = "dg==" } }));
        subject.OnNext(SetEvent("3", new Dictionary<string, object?> { [KnownFieldNames.VersionKey] = 0L, [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, string?> { ["f"] = "dg==" } }));
        subject.OnNext(new TestCacheEvent { Id = "4", Type = KnownEventTypes.CacheRemoved, Source = new Uri("urn:peer"), Data = new CacheEventData("k") });

        offered.Should().Be(0);
        token.HasChanged.Should().BeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task An_own_write_that_returns_at_or_below_the_held_count_leaves_the_held_copy_as_it_is(int offset)
    {
        using var c = new PeerCluster { AutoDeliver = false };
        var node = c.Add("a");
        var (held, n) = await Held(c, node);

        // The held copy already stands on this write: its older field values must not be laid over it.
        node.Inner.ReportsVersion = _ => n + offset;
        await Add(node, ("a", "old"), ("c", "3"));

        var after = Local(c, node);
        after.Should().BeSameAs(held);
        after.Value.Should().BeEquivalentTo(Fields(("a", "1"), ("b", "2")));
        HashVersions.TryGet(after, out var stamp).Should().BeTrue();
        stamp!.Number.Should().Be(n);
        stamp.Own.Should().BeEmpty();
    }

    [Fact]
    public async Task An_own_write_one_past_the_held_count_is_laid_over_the_held_copy_and_stamped()
    {
        using var c = new PeerCluster { AutoDeliver = false };
        var node = c.Add("a");
        var (_, n) = await Held(c, node);

        await Add(node, ("a", "new"), ("c", "3"));

        var after = Local(c, node);
        after.Value.Should().BeEquivalentTo(Fields(("a", "new"), ("b", "2"), ("c", "3")));
        HashVersions.TryGet(after, out var stamp).Should().BeTrue();
        stamp!.TryResolve(out var number, out var ahead).Should().BeTrue();
        number.Should().Be(n + 1);
        ahead.Should().BeEmpty();
    }

    [Fact]
    public async Task An_own_write_at_or_below_the_held_count_of_another_epoch_is_merged_into_a_copy_no_version_stands_on()
    {
        using var c = new PeerCluster { AutoDeliver = false };
        var node = c.Add("a");
        var (_, n) = await Held(c, node);

        node.Inner.ReportsVersion = _ => (3L << HashVersion.CounterBits) + 1;
        await Add(node, ("a", "old"), ("c", "3"));

        var after = Local(c, node);
        after.Value.Should().BeEquivalentTo(Fields(("a", "old"), ("b", "2"), ("c", "3")));
        HashVersions.TryGet(after, out var stamp).Should().BeTrue();
        stamp!.Number.Should().Be(n);
        stamp.TryResolve(out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_uncounted_own_write_is_merged_into_the_held_copy_without_a_stamp()
    {
        using var c = new PeerCluster { AutoDeliver = false };
        var node = c.Add("a");
        await Held(c, node);

        node.Inner.ReportsVersion = _ => 0;
        await Add(node, ("a", "old"), ("c", "3"));

        var after = Local(c, node);
        after.Value.Should().BeEquivalentTo(Fields(("a", "old"), ("b", "2"), ("c", "3")));
        HashVersions.TryGet(after, out _).Should().BeFalse();
    }

    private static TestCacheEvent SetEvent(string id, Dictionary<string, object?> properties) =>
        new() { Id = id, Type = KnownEventTypes.CacheSet, Source = new Uri("urn:peer"), Data = new CacheEventData("k", properties) };

    private static (KeyedSubject<ICacheEvent> Subject, ChangeToken<byte[]> Token) Token()
    {
        var subject = new KeyedSubject<ICacheEvent>(NullLogger.Instance);
        var topic = Substitute.For<ITopic<ICacheEvent>>();
        topic.TopicKey.Returns(new TopicKey("t"));
        topic.Subscribe(Arg.Any<IObserver<ICacheEvent>>()).Returns(call => subject.Subscribe(call.Arg<IObserver<ICacheEvent>>()));
        return (subject, new ChangeToken<byte[]>("k", topic, new Uri("urn:me"), new SystemJsonByteSerializerProxy(), NullLogger<ChangeToken<byte[]>>.Instance, Substitute.For<ICachingTelemetryProvider>()));
    }

    /// <summary>Seeds the hash in a fixed epoch, so the node holds a copy stamped at the hash's count.</summary>
    private static async Task<(ICacheEntry<IDictionary<string, string?>> Held, long Number)> Held(PeerCluster c, PeerCluster.Node node)
    {
        c.Redis.NextEpoch = () => 5L << HashVersion.CounterBits;
        await Seed(node);
        (await node.Cache.GetAsync<string>("k", Awaited, Ct)).Should().HaveCount(2);
        var held = Local(c, node);
        HashVersions.NumberOf(held).Should().Be(c.Redis.Version());
        return (held, c.Redis.Version());
    }

    private static ICacheEntry<IDictionary<string, string?>> Local(PeerCluster c, PeerCluster.Node node)
    {
        node.Local.TryGetValue(c.Redis.Key(), out ICacheEntry<IDictionary<string, string?>>? held).Should().BeTrue();
        return held!;
    }

    private static async Task Race(Barrier barrier, PeerCluster.Node node, string field)
    {
        barrier.SignalAndWait(Ct);
        await Add(node, (field, "x"));
    }

    private static Dictionary<string, string?> Fields(params (string Field, string Value)[] fields) =>
        fields.ToDictionary(f => f.Field, f => (string?)f.Value);

    private static Task Seed(PeerCluster.Node node) => Seed(node, Awaited);

    private static async Task Seed(PeerCluster.Node node, CachePolicy policy) =>
        (await node.Cache.SetAsync<string>("k", Fields(("a", "1"), ("b", "2")), policy, Ct)).Should().BeTrue();

    private static Task Add(PeerCluster.Node node, params (string Field, string Value)[] fields) => Add(node, Awaited, fields);

    private static async Task Add(PeerCluster.Node node, CachePolicy policy, params (string Field, string Value)[] fields) =>
        (await node.Cache.SetAsync<string>("k", Fields(fields), Merge, policy, Ct)).Should().BeTrue();
}
