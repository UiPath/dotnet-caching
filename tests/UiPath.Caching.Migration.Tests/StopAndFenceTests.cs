using UiPath.Caching.Broadcast;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class StopAndFenceTests : RouterTestBase
{
    private const int Stripes = 1024;

    [Fact]
    public async Task A_factory_write_that_is_being_sent_holds_back_a_mutation_of_the_same_key()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.LibraryBackend.InFlight = async _ =>
        {
            inFlight.TrySetResult();
            await release.Task;
        };

        var leader = node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("old")), default, Ct).AsTask();
        await inFlight.Task.WaitAsync(Ct);
        node.LibraryBackend.InFlight = null;
        var set = node.Cache.SetAsync("k", new Item("new"), default, Ct).AsTask();
        set.IsCompleted.Should().BeFalse();

        node.LibraryBackend.Writes.Should().Be(1, "the mutation waits for the factory's write instead of racing it");
        release.SetResult();
        await leader;
        await set;
        node.LibraryBackend.Writes.Should().Be(2);
        Store["k"].Should().Be(new Item("new"), "the mutation is the later write");
    }

    [Fact]
    public async Task A_factory_that_finishes_while_a_mutation_is_being_sent_does_not_write_over_it()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        var factoryGate = new TaskCompletionSource();
        var factoryReturning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Item?> Factory(CancellationToken token)
        {
            factoryEntered.TrySetResult();
            await factoryGate.Task;
            factoryReturning.TrySetResult();
            return new Item("old");
        }

        var leader = node.Cache.GetOrAddAsync("k", Factory, default, Ct).AsTask();
        await factoryEntered.Task;
        node.LibraryBackend.InFlight = async _ =>
        {
            inFlight.TrySetResult();
            await release.Task;
        };
        var set = node.Cache.SetAsync("k", new Item("new"), default, Ct).AsTask();
        await inFlight.Task;
        factoryGate.SetResult();
        await factoryReturning.Task.WaitAsync(Ct);
        leader.IsCompleted.Should().BeFalse();
        release.SetResult();

        await set;
        (await leader).Should().Be(new Item("old"));
        node.LibraryBackend.Writes.Should().Be(1, "the factory saw the key mutated and did not send");
        Store["k"].Should().Be(new Item("new"));
    }

    [Fact]
    public async Task The_final_eviction_of_a_pending_key_detaches_a_load_that_started_while_it_was_pending()
    {
        var node = NewNode(localCap: TimeSpan.Zero, pendingWindow: TimeSpan.FromSeconds(5));
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        await node.Cache.SetAsync("k", new Item("unknown"), default, Ct);
        node.LibraryBackend.WriteOutcome = WriteOutcome.Committed;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Item?> Older(CancellationToken token)
        {
            entered.TrySetResult();
            await gate.Task;
            return new Item("older");
        }

        var leader = node.Cache.GetOrAddAsync("k", Older, default, Ct).AsTask();
        await entered.Task;
        await RunBackgroundAsync(node, TimeSpan.FromSeconds(6));

        var next = await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("fresh")), default, Ct);
        gate.SetResult();
        await leader;

        next.Should().Be(new Item("fresh"), "the caller after the final eviction did not wait for the older load");
        Store["k"].Should().Be(new Item("fresh"), "the older load did not write over it");
    }

    [Fact]
    public async Task A_pending_key_that_is_begun_again_while_timers_fire_keeps_its_protection()
    {
        var node = NewNode();
        var pending = new PendingMutations(node.Core, "stress", _ => ValueTask.CompletedTask, _ => { });
        var window = TimeSpan.FromMilliseconds(20);
        using var stop = new CancellationTokenSource();
        var clockTask = Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    Clock.Advance(TimeSpan.FromMilliseconds(5));
                    await Task.Yield();
                }
            },
            Ct);

        var violations = 0;
        for (var round = 0; round < 3000; round++)
        {
            var deadline = Clock.GetUtcNow() + window;
            pending.Begin("k", window);
            var mark = node.Core.Tier.Capture(new LocalKey("stress", "k"), new CacheEpoch());
            var filled = node.Core.Tier.TryFill(new LocalKey("stress", "k"), mark, new CacheEpoch(), new object(), Clock.GetUtcNow() + TimeSpan.FromMinutes(1));
            if (filled && Clock.GetUtcNow() < deadline)
            {
                violations++;
            }

            node.Core.Tier.Evict(new LocalKey("stress", "k"));
        }

        await stop.CancelAsync();
        await clockTask;

        violations.Should().Be(0, "a fill never lands while a begun window is still open");
    }

    [Fact]
    public async Task A_write_cancelled_in_flight_keeps_the_settle_bound_of_its_client()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeMigrationCache
        {
            Write = async (_, token) =>
            {
                entered.TrySetResult();
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token);
                return StoreOutcome.Committed;
            },
        };
        var node = NewNode(pendingWindow: TimeSpan.FromSeconds(5), library: new LibraryPathClient<Item>(store, new FakeConnection(), key => key, TimeSpan.FromSeconds(60)));
        using var cts = new CancellationTokenSource();

        var write = node.Cache.SetAsync("k", new Item("v"), default, cts.Token).AsTask();
        await entered.Task;
        await cts.CancelAsync();
        await write.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        Bus.Published.Should().Be(1);
        await node.Time.WaitForTimerAsync(TimeSpan.FromSeconds(60), Ct);
        Clock.Advance(TimeSpan.FromSeconds(30));

        Bus.Published.Should().Be(1, "the library command can still land for up to a minute");
        Clock.Advance(TimeSpan.FromSeconds(31));
        await node.Core.DrainAsync().WaitAsync(Ct);
        Bus.Published.Should().Be(2);
    }

    [Fact]
    public async Task Stop_waits_for_a_call_that_is_running_before_it_flushes_or_disposes()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.LibraryBackend.InFlight = async _ =>
        {
            inFlight.TrySetResult();
            await release.Task;
        };
        var write = node.Cache.SetAsync("k", new Item("v"), default, Ct).AsTask();
        await inFlight.Task;

        var stop = node.StartStop(Ct);
        await node.Time.WaitForTimerWhileRunningAsync(TimeSpan.FromSeconds(5), stop, Ct);

        stop.IsCompleted.Should().BeFalse("a call is still running");
        var late = async () => await node.Cache.RemoveAsync("k", Ct);
        await late.Should().ThrowAsync<ObjectDisposedException>();
        release.SetResult();
        (await write).Should().Be(WriteOutcome.Committed);
        await stop;
        node.Core.OperationsDrained.IsCompletedSuccessfully.Should().BeTrue();
        Bus.Published.Should().Be(1, "the running call settled and announced before the router shut down");
        Store["k"].Should().Be(new Item("v"));
    }

    [Fact]
    public async Task Stop_keeps_a_pending_key_protected_until_its_own_settle_time()
    {
        var node = NewNode(pendingWindow: TimeSpan.FromSeconds(2));
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        var started = Clock.GetUtcNow();
        var publishedAt = new List<DateTimeOffset>();
        Bus.OnPublish = _ => publishedAt.Add(Clock.GetUtcNow());

        await node.Time.WaitForTimerAsync(TimeSpan.FromSeconds(2), Ct);
        var stop = node.StartStop(Ct);
        Clock.Advance(TimeSpan.FromSeconds(2));
        await stop.WaitAsync(Ct);

        Bus.Events.Should().NotContain(ev => ev.Type == KnownEventTypes.TopicInvalidated, "the key settled inside the stop limit");
        publishedAt.Should().ContainSingle("only the final announcement is left");
        publishedAt[0].Should().BeOnOrAfter(started + TimeSpan.FromSeconds(2), "the key stayed protected until its own settle time");
    }

    [Fact]
    public async Task A_dispose_waits_for_a_pending_window_to_its_deadline_past_the_cleanup_bound()
    {
        var node = NewNode(pendingWindow: TimeSpan.FromMinutes(10), cleanupBound: TimeSpan.FromMilliseconds(300));
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        await node.Cache.SetAsync("k", new Item("v"), default, Ct);
        var started = Clock.GetUtcNow();
        DateTimeOffset? finalAnnouncement = null;
        Bus.OnPublish = message =>
        {
            if (message.Type == KnownEventTypes.CacheRemoved)
            {
                finalAnnouncement = Clock.GetUtcNow();
            }
        };

        await node.Time.WaitForTimerAsync(TimeSpan.FromMinutes(10), Ct);
        var dispose = node.StartDispose();
        var stop = node.StartStop(Ct);
        await node.Time.WaitForTimerWhileRunningAsync(TimeSpan.FromSeconds(5), stop, Ct);
        Clock.Advance(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(Ct);
        dispose.IsCompleted.Should().BeFalse();
        Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(5));
        await dispose.WaitAsync(Ct);

        (Clock.GetUtcNow() - started).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(10));
        finalAnnouncement.Should().Be(started + TimeSpan.FromMinutes(10), "the final announcement waits for the pending window");
        Bus.Events.Should().Contain(ev => ev.Type == KnownEventTypes.TopicInvalidated, "the bounded stop still announced the cache-level invalidation");
    }

    [Fact]
    public async Task A_cancelled_first_stop_does_not_cancel_the_cleanup_or_hide_it_from_the_next_caller()
    {
        var node = NewNode(pendingWindow: TimeSpan.FromMinutes(5));
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        await node.Cache.SetAsync("k", new Item("v"), default, Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var first = async () => await node.StartStop(cts.Token);
        await first.Should().ThrowAsync<OperationCanceledException>();
        await node.StopAsync();

        Bus.Events.Should().Contain(ev => ev.Type == KnownEventTypes.TopicInvalidated);
        await RunBackgroundAsync(node, TimeSpan.FromMinutes(1));
        node.Core.IsIdle.Should().BeTrue();
        node.Core.IsStopped.Should().BeTrue();
    }

    [Fact]
    public async Task The_whole_cleanup_is_bounded_when_publishing_hangs()
    {
        var node = NewNode(pendingWindow: TimeSpan.FromMinutes(5));
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        await node.Cache.SetAsync("k", new Item("v"), default, Ct);
        Bus.Hold = new TaskCompletionSource();

        var stop = node.StopAsync();

        await stop.WaitAsync(Ct);
        Bus.Hold.Task.IsCompleted.Should().BeFalse("the cleanup ended on its bound while publishing still hung");
        node.Core.IsStopped.Should().BeTrue();
        Bus.Hold.SetResult();
    }

    [Fact]
    public async Task A_mutation_of_another_key_in_the_same_stripe_does_not_suppress_a_factory_write()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        var other = $"other{Guid.NewGuid():N}"[..14];
        var sibling = NewNode(cacheName: other, sharing: node);
        var neighbour = Enumerable.Range(0, 100000).Select(i => $"n{i}").First(candidate => StripeOf(node.CacheName, candidate) == StripeOf(node.CacheName, "k"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Item?> Factory(CancellationToken token)
        {
            entered.TrySetResult();
            await gate.Task;
            return new Item("made");
        }

        var leader = node.Cache.GetOrAddAsync("k", Factory, default, Ct).AsTask();
        await entered.Task;
        await node.Cache.SetAsync(neighbour, new Item("neighbour"), default, Ct);
        await sibling.Cache.SetAsync(neighbour, new Item("sibling"), default, Ct);
        gate.SetResult();
        await leader;

        Store["k"].Should().Be(new Item("made"), "the factory's own write was sent, whatever the other cache did in the same stripe");
        node.LibraryBackend.Writes.Should().Be(2, "the neighbour's write and the factory's own");
        node.Cache.HeldKeys.Should().Be(0, "no key state outlives its calls");
    }

    [Fact]
    public async Task A_clear_waits_for_the_writes_under_way_and_runs_alone()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.LibraryBackend.InFlight = async _ =>
        {
            inFlight.TrySetResult();
            await release.Task;
        };
        var write = node.Cache.SetAsync("k", new Item("v"), default, Ct).AsTask();
        await inFlight.Task;
        node.LibraryBackend.InFlight = null;

        var clearing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishClear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.SourceBackend.BeforeClear = () =>
        {
            clearing.TrySetResult();
            return finishClear.Task;
        };
        var clear = node.Cache.ClearAsync(Ct).AsTask();
        clear.IsCompleted.Should().BeFalse();

        node.SourceBackend.Clears.Should().Be(0, "the scan starts only once the write under way has finished");
        var later = node.Cache.SetAsync("other", new Item("v"), default, Ct).AsTask();
        later.IsCompleted.Should().BeFalse();
        node.LibraryBackend.Writes.Should().Be(1, "a write that starts during the clear waits for it");
        release.SetResult();
        await clearing.Task.WaitAsync(Ct);
        try
        {
            later.IsCompleted.Should().BeFalse("the clear is now scanning");
            Store.Should().ContainKey("k", "the first write landed before the clear scanned");
        }
        finally
        {
            finishClear.TrySetResult();
        }

        await write;
        await clear;
        await later;
        node.SourceBackend.Clears.Should().Be(1);
        Store.Should().NotContainKey("k");
        Store.Should().ContainKey("other");
    }

    private static int StripeOf(string cache, string key) => (int)((uint)new LocalKey(cache, key).Hash() % Stripes);
}
