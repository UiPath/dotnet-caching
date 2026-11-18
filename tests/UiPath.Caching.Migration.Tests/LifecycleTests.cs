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
        Bus.Events[^1].Type.Should().Be(KnownEventTypes.CacheRemoved, "then the final announcement of the key");
        peer.IsLocal("k").Should().BeFalse();
        writer.Core.IsIdle.Should().BeTrue();
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

        (await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(3), Ct))).Should().BeSameAs(stop);
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
            await Task.Delay(Timeout.Infinite, token);
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
