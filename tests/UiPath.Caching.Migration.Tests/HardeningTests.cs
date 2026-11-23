using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class HardeningTests : RouterTestBase
{
    [Fact]
    public async Task A_peer_event_between_capture_and_fill_rejects_the_fill()
    {
        var node = NewNode();
        var peer = NewNode();
        Store["k"] = new Item("old");
        node.LibraryBackend.AfterRead = async () =>
        {
            node.LibraryBackend.AfterRead = null;
            await peer.Cache.SetAsync("k", new Item("newer"), default, Ct);
        };

        var read = await node.Cache.TryGetAsync("k", Ct);

        read.Should().Be(new Item("old"));
        node.IsLocal("k").Should().BeFalse("the peer's event came after the read began");
    }

    [Fact]
    public async Task A_clear_between_capture_and_fill_rejects_the_fill()
    {
        var node = NewNode();
        var peer = NewNode();
        Store["k"] = new Item("old");
        node.LibraryBackend.AfterRead = async () =>
        {
            node.LibraryBackend.AfterRead = null;
            await peer.Cache.ClearAsync(Ct);
        };

        await node.Cache.TryGetAsync("k", Ct);

        node.IsLocal("k").Should().BeFalse("the clear advanced the epoch after the read began");
    }

    [Fact]
    public async Task A_clear_cancelled_while_it_waits_for_the_writes_under_way_leaves_the_cache_usable()
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
        using var cts = new CancellationTokenSource();
        var clear = node.Cache.ClearAsync(cts.Token).AsTask();
        await Task.Delay(100, Ct);

        await cts.CancelAsync();
        await clear.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        var other = node.Cache.SetAsync("other", new Item("v"), default, Ct).AsTask();

        (await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(3), Ct))).Should().BeSameAs(other, "no clear is left holding new writes back");
        release.SetResult();
        await write;
        await node.Cache.ClearAsync(Ct);
        node.SourceBackend.Clears.Should().Be(1, "the cancelled clear never scanned, and the next one is not blocked by it");
    }

    [Fact]
    public async Task A_token_cancelled_before_the_library_sends_does_not_reach_the_source()
    {
        using var cts = new CancellationTokenSource();
        var library = new CancelledBeforeSend(cts);
        var node = NewNode(library: library);

        var write = async () => await node.Cache.SetAsync("k", new Item("v"), default, cts.Token);

        await write.Should().ThrowAsync<OperationCanceledException>();
        node.SourceBackend.Writes.Should().Be(0, "the second client would be sent a cancelled token");
        Bus.Published.Should().Be(0);
        node.Core.BackgroundCount.Should().Be(0, "nothing was in doubt");
    }

    [Fact]
    public async Task A_publish_that_throws_a_cancellation_is_a_failed_publish_that_is_retried()
    {
        var node = NewNode();
        Bus.FailWith = new TaskCanceledException();

        var outcome = await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        outcome.Should().Be(WriteOutcome.Committed, "the write committed, whatever the publish did");
        node.Cache.Retrier.WaitingKeys.Should().Be(1);
        Bus.FailWith = null;
        await RunBackgroundAsync(node, TimeSpan.FromSeconds(1));
        Bus.Published.Should().Be(1, "the retry ran and published");
        node.Cache.Retrier.WaitingKeys.Should().Be(0);
    }

    [Fact]
    public async Task A_retrier_whose_publish_throws_a_cancellation_keeps_working()
    {
        var node = NewNode();
        Bus.FailPublish = true;
        await node.Cache.SetAsync("k", new Item("v"), default, Ct);
        Bus.FailPublish = false;
        Bus.FailWith = new TaskCanceledException();
        for (var round = 0; round < 3; round++)
        {
            await Task.Delay(50, Ct);
            Clock.Advance(TimeSpan.FromSeconds(6));
        }

        await Task.Delay(50, Ct);
        Bus.FailWith = null;

        await RunBackgroundAsync(node, TimeSpan.FromSeconds(1));

        Bus.Published.Should().Be(1);
        node.Cache.Retrier.WaitingKeys.Should().Be(0);
    }

    [Fact]
    public async Task A_retrier_whose_publish_callback_throws_a_cancellation_keeps_its_key_and_its_worker()
    {
        var core = new RouterCore(Clock, _ => true, NullLogger.Instance);
        var attempts = 0;
        var retrier = new InvalidationRetrier(
            core,
            TimeSpan.FromMinutes(1),
            _ => ++attempts == 1 ? throw new TaskCanceledException() : ValueTask.FromResult(true),
            () => ValueTask.FromResult(true),
            _ => { });

        retrier.ReportKey("k", new Route(RoutePath.Library, Route.Flag));
        for (var round = 0; round < 40 && retrier.WaitingKeys > 0; round++)
        {
            await Task.Delay(30, Ct);
            Clock.Advance(TimeSpan.FromSeconds(6));
        }

        retrier.WaitingKeys.Should().Be(0, "the second attempt ran and published");
        attempts.Should().Be(2);
        core.BeginStop();
        core.CancelBackground();
        await core.DrainAsync();
        core.DisposeResources();
    }

    [Fact]
    public async Task A_clock_that_moves_between_the_prune_and_the_wait_does_not_end_the_retrier()
    {
        var ticking = new TickingTimeProvider(Clock.GetUtcNow(), TimeSpan.FromMilliseconds(100));
        var core = new RouterCore(ticking, _ => true, NullLogger.Instance);
        var retrier = new InvalidationRetrier(core, TimeSpan.FromMilliseconds(150), _ => ValueTask.FromResult(true), () => ValueTask.FromResult(true), _ => { });

        retrier.ReportKey("k", new Route(RoutePath.Library, Route.Flag));

        var until = DateTime.UtcNow.AddSeconds(5);
        while (!core.IsIdle && DateTime.UtcNow < until)
        {
            await Task.Delay(10, Ct);
        }

        core.IsIdle.Should().BeTrue();
        retrier.WaitingKeys.Should().Be(0, "the worker pruned the expired key instead of dying on a negative wait");
        // A new report must start a worker. With this clock the worker prunes the key and ends at once, so the
        // count of running workers is a race; the key's removal is the proof, since only a worker removes it.
        retrier.ReportKey("j", new Route(RoutePath.Library, Route.Flag));
        until = DateTime.UtcNow.AddSeconds(5);
        while ((retrier.WaitingKeys != 0 || !core.IsIdle) && DateTime.UtcNow < until)
        {
            await Task.Delay(10, Ct);
        }

        retrier.WaitingKeys.Should().Be(0, "the running flag was released, so the new report started a worker that pruned the key");
        core.BeginStop();
        core.CancelBackground();
        await core.DrainAsync();
        core.DisposeResources();
    }

    [Fact]
    public async Task The_final_eviction_detaches_the_key_under_the_pending_gate()
    {
        var node = NewNode();
        PendingMutations? pending = null;
        var blocked = false;
        pending = new PendingMutations(
            node.Core,
            "gate",
            _ => ValueTask.CompletedTask,
            _ => blocked = !Task.Run(() => pending!.Count, Ct).Wait(300, Ct));

        pending.Begin("k", TimeSpan.FromSeconds(5));
        await RunBackgroundAsync(node, TimeSpan.FromSeconds(6));

        blocked.Should().BeTrue("a load validated between the retire and the detach would still write");
    }

    [Fact]
    public async Task A_library_that_cannot_answer_while_the_source_is_absent_writes_the_loaded_value_to_the_source()
    {
        var node = NewNode();
        node.LibraryBackend.ReadOverride = ReadOutcome.Unavailable;

        var value = await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made")), default, Ct);

        value.Should().Be(new Item("made"));
        node.SourceBackend.Writes.Should().Be(1);
        node.LibraryBackend.Writes.Should().Be(0, "the library just failed");
    }

    [Fact]
    public async Task The_write_after_a_library_failure_falls_back_to_the_library_when_the_source_does_not_send()
    {
        var node = NewNode();
        node.LibraryBackend.ReadOverride = ReadOutcome.Unavailable;
        node.SourceBackend.WriteOutcome = WriteOutcome.NotSent;

        await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made")), default, Ct);

        node.SourceBackend.Writes.Should().Be(1);
        node.LibraryBackend.Writes.Should().Be(1);
    }

    [Fact]
    public void A_pending_window_above_a_day_is_refused()
    {
        var add = () => new RouterBuilder().Add(new RouterCacheRegistration<Item>
        {
            Name = "c",
            Group = "g",
            Source = new FakeBackend([], Clock),
            RedisKey = key => key,
            PendingWindow = TimeSpan.FromDays(2),
        });

        add.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(172800)]
    public void An_explicit_settle_bound_outside_zero_to_a_day_is_refused(int seconds)
    {
        var set = () => new RouterBuilder().UseLibrarySettleBound(TimeSpan.FromSeconds(seconds));

        set.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_settle_bound_follows_the_live_configuration_a_configurator_produced()
    {
        var bound = LibrarySettleBound.Compute(TimeSpan.FromSeconds(1), "host:6379,asyncTimeout=45000,syncTimeout=45000");

        bound.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(46));
    }

    [Fact]
    public void The_settle_bound_includes_the_maintenance_relaxed_timeout()
    {
        var bound = LibrarySettleBound.Compute(TimeSpan.FromSeconds(1), "host:6379,asyncTimeout=5000,maintRelaxedTimeout=120");

        bound.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(121));
    }

    [Fact]
    public void A_configuration_that_was_never_seen_or_cannot_be_read_takes_the_ceiling()
    {
        LibrarySettleBound.Compute(TimeSpan.Zero, null).Should().Be(LibrarySettleBound.Ceiling);
        LibrarySettleBound.Compute(TimeSpan.Zero, "host:6379,asyncTimeout=notanumber").Should().Be(LibrarySettleBound.Ceiling);
    }

    [Fact]
    public void A_configuration_beyond_the_horizon_is_refused_at_startup()
    {
        var check = () => LibrarySettleBound.ThrowIfBeyondHorizon(LibrarySettleBound.Join("host:6379", "asyncTimeout=100000000"));

        check.Should().Throw<InvalidOperationException>().WithMessage("*longer than the 24 hours*");
        LibrarySettleBound.ThrowIfBeyondHorizon("host:6379,asyncTimeout=60000");
    }

    [Fact]
    public void A_snapshot_is_kept_once_seen_and_never_read_again()
    {
        var live = "host:6379,asyncTimeout=45000";
        var reads = 0;
        var snapshot = new LibrarySettleSnapshot(() => TimeSpan.FromSeconds(1), () =>
        {
            reads++;
            return live;
        });

        snapshot.Current().Should().Be(TimeSpan.FromSeconds(46));
        live = "host:6379,asyncTimeout=5000";
        snapshot.Current().Should().Be(TimeSpan.FromSeconds(46));
        reads.Should().Be(1);
    }

    [Fact]
    public void A_snapshot_that_cannot_be_taken_yet_takes_the_ceiling_and_tries_again()
    {
        var live = (string?)null;
        var snapshot = new LibrarySettleSnapshot(() => TimeSpan.Zero, () => live);

        snapshot.Current().Should().Be(LibrarySettleBound.Ceiling);
        live = "host:6379,asyncTimeout=45000";
        snapshot.Current().Should().Be(TimeSpan.FromSeconds(45));
        new LibrarySettleSnapshot(() => TimeSpan.Zero, () => throw new InvalidOperationException()).Current().Should().Be(LibrarySettleBound.Ceiling);
    }
    [Fact]
    public async Task Dispose_waits_for_the_deadline_announcement_of_a_pending_command()
    {
        var node = NewNode(pendingWindow: TimeSpan.FromMinutes(5));
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        var dispose = node.StartDispose();
        for (var round = 0; round < 4; round++)
        {
            await Task.Delay(50, Ct);
            Clock.Advance(TimeSpan.FromSeconds(6));
        }

        await Task.Delay(100, Ct);
        dispose.IsCompleted.Should().BeFalse("the connector and topics the announcement needs are released after dispose returns");
        await RunBackgroundAsync(node, TimeSpan.FromMinutes(1));
        await dispose.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Bus.Events[^1].Type.Should().Be(UiPath.Caching.Broadcast.KnownEventTypes.CacheRemoved);
    }

    [Fact]
    public async Task A_final_announcement_that_fails_after_stop_began_is_still_retried()
    {
        var node = NewNode(pendingWindow: TimeSpan.FromSeconds(5));
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        await node.Cache.SetAsync("k", new Item("v"), default, Ct);
        Bus.Hold = new TaskCompletionSource();
        Bus.FailNext = 1;
        var attempts = Bus.Attempts;
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!Clock.HasTimers && DateTime.UtcNow < until)
        {
            await Task.Delay(5, Ct);
        }

        Clock.Advance(TimeSpan.FromSeconds(6));
        while (Bus.Attempts == attempts && DateTime.UtcNow < until)
        {
            await Task.Delay(5, Ct);
        }

        var stop = node.StartStop(Ct);
        await Task.Delay(150, Ct);
        stop.IsCompleted.Should().BeFalse("the final announcement is still in flight");
        Bus.Hold.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Bus.Events.Count(ev => ev.Type == UiPath.Caching.Broadcast.KnownEventTypes.CacheRemoved).Should().Be(1, "the failed announcement was retried by the flush before background work was cancelled");
    }

    private sealed class CancelledBeforeSend(CancellationTokenSource cts) : IPathClient<Item>
    {
        public TimeSpan? SettleWithin => TimeSpan.FromSeconds(60);

        public ValueTask<PathRead<Item>> ReadAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult(PathRead<Item>.Of(ReadOutcome.Absent));

        public async ValueTask<WriteResult> SetAsync(string key, Item value, DateTimeOffset? deadline, CancellationToken cancellationToken)
        {
            await cts.CancelAsync();
            return WriteOutcome.NotSent;
        }

        public ValueTask<AddResult> TryAddAsync(string key, Item value, DateTimeOffset? deadline, CancellationToken cancellationToken) => ValueTask.FromResult<AddResult>(AddOutcome.NotSent);

        public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<WriteResult>(WriteOutcome.NotSent);
    }
}
