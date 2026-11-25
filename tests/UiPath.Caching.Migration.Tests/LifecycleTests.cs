using UiPath.Caching.Broadcast;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class LifecycleTests : RouterTestBase
{
    [Fact]
    public async Task Stop_runs_the_final_eviction_and_announcement_of_a_pending_key()
    {
        var writer = NewNode(pendingWindow: TimeSpan.FromMinutes(5));
        var peer = NewNode();
        Store["k"] = new Item("old");
        writer.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        await writer.Cache.SetAsync("k", new Item("new"), default, Ct);
        await peer.Cache.TryGetAsync("k", Ct);
        peer.IsLocal("k").Should().BeTrue();
        writer.LibraryBackend.Land("k", new Item("new"));
        Bus.Published.Should().Be(1);

        await writer.StopAsync();

        Bus.Events.Should().ContainSingle(ev => ev.Type == KnownEventTypes.TopicInvalidated, "the pending write outlived the stop limit, so a cache-level invalidation goes out");
        writer.Core.IsIdle.Should().BeFalse("the key keeps its own timer until the command can no longer land");
        await RunBackgroundAsync(writer, TimeSpan.FromMinutes(1));

        Bus.Events[^1].Type.Should().Be(KnownEventTypes.CacheRemoved, "then the final announcement of the key, at its own deadline");
        peer.IsLocal("k").Should().BeFalse();
        writer.Core.IsIdle.Should().BeTrue();
    }

    [Fact]
    public async Task A_commit_that_lands_after_the_stop_announcement_is_still_evicted_and_announced()
    {
        var writer = NewNode(pendingWindow: TimeSpan.FromMinutes(5));
        var peer = NewNode();
        Store["k"] = new Item("old");
        writer.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        await writer.Cache.SetAsync("k", new Item("new"), default, Ct);

        await writer.StopAsync();
        await peer.Cache.TryGetAsync("k", Ct);
        writer.LibraryBackend.Land("k", new Item("new"));
        peer.IsLocal("k").Should().BeTrue("a peer refilled the old value after the stop's announcement");
        await RunBackgroundAsync(writer, TimeSpan.FromMinutes(1));

        peer.IsLocal("k").Should().BeFalse("the late commit was announced when its deadline passed");
        Bus.Events[^1].Type.Should().Be(KnownEventTypes.CacheRemoved);
    }

    [Fact]
    public async Task A_call_that_outlives_the_drain_limit_settles_before_anything_is_disposed()
    {
        var node = NewNode(pendingWindow: TimeSpan.FromSeconds(30));
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        node.LibraryBackend.InFlight = async _ =>
        {
            inFlight.TrySetResult();
            await release.Task;
        };
        var write = node.Cache.SetAsync("k", new Item("v"), default, Ct).AsTask();
        await inFlight.Task;

        await node.StopAsync();
        var tier = () => node.Core.Tier.TryGet(new LocalKey(node.CacheName, "k"), node.Cache.Epoch, out _);
        tier.Should().NotThrow("the straggler still uses the tier");
        release.SetResult();

        (await write).Should().Be(WriteOutcome.Unknown);
        await RunBackgroundAsync(node, TimeSpan.FromSeconds(31));
        Bus.Events[^1].Type.Should().Be(KnownEventTypes.CacheRemoved, "the pending key the straggler began retires at its deadline");
        await node.Router.DisposeAsync().AsTask().WaitAsync(Ct);
        tier.Should().Throw<ObjectDisposedException>("the tier is disposed after the straggler ends");
    }

    [Fact]
    public async Task A_publish_that_hangs_does_not_hold_back_the_next_write_of_the_key()
    {
        var node = NewNode();
        Bus.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Bus.OnAttempt = () => publishing.TrySetResult();
        var first = node.Cache.SetAsync("k", new Item("one"), default, Ct).AsTask();
        await publishing.Task.WaitAsync(Ct);
        first.IsCompleted.Should().BeFalse();

        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.LibraryBackend.InFlight = _ =>
        {
            secondEntered.TrySetResult();
            return Task.CompletedTask;
        };
        var second = node.Cache.SetAsync("k", new Item("two"), default, Ct).AsTask();
        try
        {
            await secondEntered.Task.WaitAsync(Ct);
            node.LibraryBackend.Writes.Should().Be(2, "the key lock is free once the command is settled locally");
        }
        finally
        {
            Bus.Hold.SetResult();
        }

        await first;
        await second;
        Store["k"].Should().Be(new Item("two"));
    }
    [Fact]
    public async Task A_call_after_stop_fails_before_anything_is_sent()
    {
        var node = NewNode();
        await node.StopAsync();

        var get = async () => await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("v")), default, Ct);
        var set = async () => await node.Cache.SetAsync("k", new Item("v"), default, Ct);
        var remove = async () => await node.Cache.RemoveAsync("k", Ct);
        var clear = async () => await node.Cache.ClearAsync(Ct);

        await get.Should().ThrowAsync<ObjectDisposedException>();
        await set.Should().ThrowAsync<ObjectDisposedException>();
        await remove.Should().ThrowAsync<ObjectDisposedException>();
        await clear.Should().ThrowAsync<ObjectDisposedException>();
        node.LibraryBackend.Reads.Should().Be(0);
        node.LibraryBackend.Writes.Should().Be(0);
        node.SourceBackend.Clears.Should().Be(0);
    }

    [Fact]
    public async Task Stop_ends_a_publish_retry_that_is_waiting_without_blocking()
    {
        var node = NewNode(localCap: TimeSpan.FromMinutes(1));
        Bus.FailPublish = true;
        await node.Cache.SetAsync("k", new Item("v"), default, Ct);
        node.Core.IsIdle.Should().BeFalse();

        var stop = node.StopAsync();

        await stop.WaitAsync(Ct);
        node.Core.IsIdle.Should().BeTrue();
    }

    [Fact]
    public async Task A_write_cancelled_while_it_is_in_flight_is_settled_as_unknown()
    {
        var node = NewNode(pendingWindow: TimeSpan.FromSeconds(30));
        Store["k"] = new Item("old");
        await node.Cache.TryGetAsync("k", Ct);
        using var cts = new CancellationTokenSource();
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.LibraryBackend.InFlight = async token =>
        {
            inFlight.TrySetResult();
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token);
        };

        var write = node.Cache.SetAsync("k", new Item("new"), default, cts.Token).AsTask();
        await inFlight.Task;
        await cts.CancelAsync();

        await write.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        node.IsLocal("k").Should().BeFalse("the command may have reached Redis");
        Bus.Published.Should().Be(1);
        node.Core.BackgroundCount.Should().Be(1, "the key is pending");
    }

    [Fact]
    public async Task A_token_cancelled_before_the_send_settles_nothing()
    {
        var node = NewNode();
        Store["k"] = new Item("old");
        await node.Cache.TryGetAsync("k", Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var write = async () => await node.Cache.SetAsync("k", new Item("new"), default, cts.Token);

        await write.Should().ThrowAsync<OperationCanceledException>();
        node.LibraryBackend.Writes.Should().Be(0);
        node.IsLocal("k").Should().BeTrue();
        Bus.Published.Should().Be(0);
    }
}
