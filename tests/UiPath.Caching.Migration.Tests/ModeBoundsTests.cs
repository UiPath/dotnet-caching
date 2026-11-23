using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class ModeBoundsTests : RouterTestBase
{
    [Fact]
    public async Task A_factory_that_never_returns_lets_the_router_resolve_in_bypass_within_the_bound_and_dispose_in_time()
    {
        var node = NewNode(lazyLibraryAsync: _ => new TaskCompletionSource<LibraryParts>().Task, startBound: TimeSpan.FromMilliseconds(300));

        node.Router.Mode.Should().Be(RouterMode.BypassLibrary);
        node.Router.ModeReason.Should().Be(RouterModeReason.LibraryUnavailable);
        await node.Cache.TryGetAsync("k", Ct);
        node.SourceBackend.Reads.Should().Be(1);

        await node.StartDispose().WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task A_factory_that_blocks_its_thread_holds_up_neither_creation_nor_dispose()
    {
        using var release = new ManualResetEventSlim();
        var node = NewNode(
            lazyLibraryAsync: token =>
            {
                release.Wait(token);
                return Task.FromResult(Parts(new FakeMigrationCache()));
            },
            startBound: TimeSpan.FromMilliseconds(300));

        try
        {
            node.Router.Mode.Should().Be(RouterMode.BypassLibrary);
            await node.StartDispose().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task A_result_that_arrives_after_the_stop_is_released_and_never_attached()
    {
        var late = new TaskCompletionSource<LibraryParts>();
        var hosted = new FakeHostedService();
        var node = NewNode(lazyLibraryAsync: _ => late.Task, startBound: TimeSpan.FromMilliseconds(200));
        await node.StartDispose().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        late.SetResult(Parts(new FakeMigrationCache(), [hosted]));

        await UntilAsync(() => hosted.Stopped == 1);
        Bus.Subscriptions.Should().Be(0, "the late stack was never attached");
    }

    [Fact]
    public async Task A_late_start_after_the_bound_leaves_bypass_by_the_normal_order()
    {
        var gate = new TaskCompletionSource<LibraryParts>();
        var node = NewNode(lazyLibraryAsync: _ => gate.Task, startBound: TimeSpan.FromMilliseconds(200));
        node.Router.Mode.Should().Be(RouterMode.BypassLibrary);
        var left = NextMode(node);

        gate.SetResult(Parts(new FakeMigrationCache()));

        (await left.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().Be(RouterMode.Library);
    }

    [Fact]
    public async Task A_leave_whose_subscription_never_confirms_stays_in_bypass_reports_it_and_is_retried()
    {
        var node = NewNode(sourceTimeout: TimeSpan.FromSeconds(5));
        await node.Router.EnterBypassAsync(Ct);
        Bus.Subscribed = false;

        var leave = node.Router.LeaveBypassAsync(Ct);
        await AdvanceWithinAsync(() => leave.IsCompleted, node, TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() => leave);
        node.Router.Mode.Should().Be(RouterMode.BypassLibrary);
        var left = NextMode(node);
        Bus.Subscribed = true;

        await AdvanceWithinAsync(() => left.IsCompleted, node, TimeSpan.FromSeconds(1));

        (await left.WaitAsync(TimeSpan.FromSeconds(1), Ct)).Should().Be(RouterMode.Library, "the background retry left once the subscription was in place");
    }

    [Fact]
    public async Task A_leave_cancelled_after_the_mode_write_still_reopens_the_tier()
    {
        var node = NewNode();
        Store["k"] = new Item("stored");
        Store["held"] = new Item("held");
        await node.Router.EnterBypassAsync(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.SourceBackend.AfterRead = async () =>
        {
            started.TrySetResult();
            await release.Task;
        };
        var held = node.Cache.TryGetAsync("held", Ct).AsTask();
        await started.Task;
        node.SourceBackend.AfterRead = null;
        using var cancel = new CancellationTokenSource();
        var leave = node.Router.LeaveBypassAsync(cancel.Token);
        await UntilAsync(() => node.Router.Mode == RouterMode.Library);

        try
        {
            await cancel.CancelAsync();
        }
        finally
        {
            release.TrySetResult();
        }

        await held;
        await leave.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await node.Cache.TryGetAsync("k", Ct);
        node.IsLocal("k").Should().BeTrue("the committed transition finished and the tier serves again");
        node.Router.Mode.Should().Be(RouterMode.Library);
    }

    [Fact]
    public async Task A_handler_that_calls_back_into_the_router_does_not_deadlock_and_notifications_keep_their_order()
    {
        var node = NewNode();
        var seen = new List<RouterMode>();
        node.Router.ModeChanged += (mode, _) =>
        {
            seen.Add(mode);
            if (mode == RouterMode.BypassLibrary)
            {
                node.Router.LeaveBypassAsync(Ct).GetAwaiter().GetResult();
            }
        };

        await node.Router.EnterBypassAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await UntilAsync(() => seen.Count == 2);
        seen.Should().Equal(RouterMode.BypassLibrary, RouterMode.Library);
        node.Router.Mode.Should().Be(RouterMode.Library);
    }

    [Fact]
    public async Task A_source_write_in_bypass_returns_while_the_library_publish_hangs_and_the_peer_is_told_once_it_does_not()
    {
        var bypassed = NewNode(bypass: () => true);
        var peer = NewNode();
        Store["k"] = new Item("old");
        await peer.Cache.TryGetAsync("k", Ct);
        peer.IsLocal("k").Should().BeTrue();
        Bus.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await bypassed.Cache.SetAsync("k", new Item("new"), default, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        bypassed.SourceBackend.Writes.Should().Be(1);
        peer.IsLocal("k").Should().BeTrue("the announcement is stuck behind the hung publish");
        Bus.Hold.SetResult();
        Bus.Hold = null;
        await RunBackgroundAsync(bypassed, TimeSpan.FromMilliseconds(100));

        peer.IsLocal("k").Should().BeFalse();
    }

    [Fact]
    public async Task A_cache_that_cannot_attach_gives_back_what_the_earlier_ones_took_and_the_retry_attaches_all()
    {
        var hosted = new FakeHostedService();
        var core = new RouterCore(Clock, _ => true, NullLogger.Instance);
        RoutedCache<Item> Make(string name) => new(core, new RouterCacheRegistration<Item> { Name = name, Group = "g", Source = new FakeBackend(Store, Clock), RedisKey = key => key });
        var router = new Router(core, [("first", typeof(Item), Make("first")), ("second", typeof(Item), Make("second"))]);
        core.UseLibrary(_ => Task.FromResult(Parts(new FakeMigrationCache(), [hosted])));
        Bus.FailCreate = new InvalidOperationException("topic");
        Bus.FailCreateFor = "second";
        var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.ModeChanged += (mode, _) =>
        {
            if (mode == RouterMode.Library)
            {
                left.TrySetResult();
            }
        };

        router.Start();

        router.Mode.Should().Be(RouterMode.BypassLibrary);
        Bus.Subscriptions.Should().Be(0, "the first cache's subscription was given back when the second failed");
        hosted.Stopped.Should().Be(1, "so were the stack's hosted services");
        Bus.FailCreate = null;
        await AdvanceWithinAsync(() => left.Task.IsCompleted, node: null, TimeSpan.FromSeconds(1), router);

        Bus.Subscriptions.Should().Be(2);
        router.Mode.Should().Be(RouterMode.Library);
        await router.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_with_a_leave_waiting_and_an_enter_queued_ends_both_and_throws_nothing_else()
    {
        var node = NewNode();
        await node.Router.EnterBypassAsync(Ct);
        Bus.Subscribed = false;
        var leave = node.Router.LeaveBypassAsync(Ct);
        await Task.Delay(100, Ct);
        var enter = node.Router.EnterBypassAsync(Ct);
        await Task.Delay(100, Ct);

        await node.StartDispose().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        foreach (var transition in new[] { leave, enter })
        {
            var outcome = await Outcome(transition.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            (outcome is null or OperationCanceledException or ObjectDisposedException).Should().BeTrue($"got {outcome}");
        }
    }

    [Fact]
    public async Task A_handler_that_blocks_on_disposing_the_router_completes()
    {
        var flag = false;
        var node = NewNode(bypass: () => flag);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Router.ModeChanged += (_, _) =>
        {
            node.StartDispose().GetAwaiter().GetResult();
            disposed.TrySetResult();
        };
        flag = true;

        await node.Cache.TryGetAsync("k", Ct);

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        disposed.Task.IsCompletedSuccessfully.Should().BeTrue("the handler's own dispose returned");
        node.Core.IsStopped.Should().BeTrue();
    }

    [Fact]
    public async Task A_hosted_service_whose_stop_never_returns_does_not_hold_up_dispose()
    {
        var node = NewNode(lazyLibrary: () => Parts(new FakeMigrationCache(), [new HangingHostedService()]), cleanupBound: TimeSpan.FromMilliseconds(300));
        node.Router.Mode.Should().Be(RouterMode.Library);

        await node.StartDispose().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        node.Core.UnfinishedCleanup.Should().Be(1, "the step is counted and left running");
    }

    [Fact]
    public async Task A_bypass_write_whose_publish_hangs_does_not_hold_up_teardown()
    {
        var node = NewNode(bypass: () => true);
        Bus.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await node.Cache.SetAsync("k", new Item("v"), default, Ct);
            await RunBackgroundStartedAsync(node);

            var teardown = node.DisposeAsync().AsTask();
            await teardown.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            teardown.IsCompletedSuccessfully.Should().BeTrue();
            node.Core.IsIdle.Should().BeTrue("no background work is left behind the stuck publish");
            Bus.Published.Should().Be(0, "the publish never went out");
        }
        finally
        {
            Bus.Hold.TrySetResult();
        }
    }

    private static async Task<Exception?> Outcome(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Gives the retrier's first publish time to start, which needs the clock to pass its first delay.</summary>
    private async Task RunBackgroundStartedAsync(TestNode node)
    {
        for (var i = 0; i < 20 && !node.Core.IsIdle; i++)
        {
            await AdvanceWhenArmedAsync(TimeSpan.FromMilliseconds(150));
            if (Bus.Attempts > 0)
            {
                return;
            }
        }
    }

    /// <summary>Moves the clock until the condition holds, with a real-time limit and a message that says what the router was doing.</summary>
    private async Task AdvanceWithinAsync(Func<bool> done, TestNode? node, TimeSpan step, Router? router = null)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!done() && DateTime.UtcNow < until)
        {
            await AdvanceWhenArmedAsync(step);
        }

        if (!done())
        {
            var mode = (node?.Router ?? router)!;
            throw new TimeoutException($"Gave up after 20 s: mode {mode.Mode}, reason {mode.ModeReason}, subscribed {Bus.Subscribed}, subscriptions {Bus.Subscriptions}, timers armed {Clock.HasTimers}, background idle {node?.Core.IsIdle}.");
        }
    }
}
