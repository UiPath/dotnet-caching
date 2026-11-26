using Microsoft.Extensions.Options;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class ModeTests : RouterTestBase
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task A_bypass_flag_puts_every_call_on_the_source_with_no_library_and_no_shared_tier()
    {
        var node = NewNode(bypass: () => true);
        Store["k"] = new Item("stored");

        (await node.Cache.TryGetAsync("k", Ct)).Should().Be(new Item("stored"));
        (await node.Cache.TryGetAsync("k", Ct)).Should().Be(new Item("stored"));
        await node.Cache.SetAsync("w", new Item("w"), default, Ct);
        await node.Cache.RemoveAsync("w", Ct);

        node.SourceBackend.Reads.Should().Be(2, "no shared-tier hit serves the second read");
        node.SourceBackend.Writes.Should().Be(1);
        node.SourceBackend.Removes.Should().Be(1);
        node.LibraryBackend.Reads.Should().Be(0);
        node.LibraryBackend.Writes.Should().Be(0);
        node.LibraryBackend.Removes.Should().Be(0);
        node.IsLocal("k").Should().BeFalse();
    }

    [Fact]
    public async Task In_bypass_an_unreadable_source_value_is_never_retried_on_the_library_and_a_refused_write_is_not_replayed_there()
    {
        var node = NewNode(bypass: () => true);
        node.SourceBackend.ReadOverride = ReadOutcome.Unreadable;
        node.SourceBackend.WriteOutcome = WriteOutcome.NotSent;

        var value = await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made")), default, Ct);

        value.Should().Be(new Item("made"));
        node.LibraryBackend.Reads.Should().Be(0);
        node.LibraryBackend.Writes.Should().Be(0);
    }

    [Theory]
    [InlineData("on", "true", "source", "bypass", 0)]
    [InlineData("on", "false", "source", "bypass", 0)]
    [InlineData("off", "true", "library", "flag", 1)]
    [InlineData("off", "false", "source", "flag", 1)]
    [InlineData("off", "throws", "source", "predicate_error", 1)]
    [InlineData("throws", "true", "source", "bypass_flag_error", 0)]
    public async Task The_precedence_is_bypass_then_the_group_predicate(string flag, string predicate, string path, string reason, int predicateReads)
    {
        using var meters = new MeterCollector(CacheName);
        var reads = 0;
        var node = NewNode(
            useLibrary: _ =>
            {
                reads++;
                return predicate == "throws" ? throw new InvalidOperationException("flags down") : predicate == "true";
            },
            bypass: () => flag == "throws" ? throw new InvalidOperationException("setting down") : flag == "on");

        await node.Cache.TryGetAsync("absent", Ct);

        meters.Count("miss", "path", path).Should().Be(1);
        meters.Count("miss", "path_reason", reason).Should().Be(1);
        reads.Should().Be(predicateReads, "a bypassed call never reads the predicate");
    }

    [Fact]
    public async Task A_bypass_flag_that_throws_changes_no_mode_and_only_that_call_takes_the_source()
    {
        var fail = true;
        var node = NewNode(bypass: () => fail ? throw new InvalidOperationException("down") : false);
        Store["k"] = new Item("stored");

        await node.Cache.TryGetAsync("k", Ct);
        fail = false;
        await node.Cache.TryGetAsync("k", Ct);

        node.Router.Mode.Should().Be(RouterMode.Library);
        node.SourceBackend.Reads.Should().Be(1);
        node.LibraryBackend.Reads.Should().Be(1);
    }

    [Fact]
    public async Task Entering_bypass_clears_the_tier_and_waits_for_calls_started_in_library_mode_which_never_fill_it_late()
    {
        var node = NewNode();
        Store["held"] = new Item("held");
        Store["warm"] = new Item("warm");
        await node.Cache.TryGetAsync("warm", Ct);
        node.IsLocal("warm").Should().BeTrue();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.LibraryBackend.AfterRead = async () =>
        {
            started.TrySetResult();
            await release.Task;
        };
        var held = node.Cache.TryGetAsync("held", Ct).AsTask();
        await started.Task;

        var readFinishedAtNotification = false;
        node.Router.ModeChanged += (_, _) => readFinishedAtNotification = release.Task.IsCompleted;
        var enter = node.Router.EnterBypassAsync(Ct);
        await node.Time.WaitForTimerWhileRunningAsync(node.Core.SourceTimeout, enter, Ct);

        enter.IsCompleted.Should().BeFalse("a call started in library mode is still running");
        node.Router.Mode.Should().Be(RouterMode.BypassLibrary, "new calls are already on the source");
        node.Router.ModeReason.Should().Be(RouterModeReason.Requested);
        node.IsLocal("warm").Should().BeFalse("the shared tier was cleared first");
        node.LibraryBackend.AfterRead = null;
        await node.Cache.TryGetAsync("warm", Ct);
        node.SourceBackend.Reads.Should().Be(1);
        node.IsLocal("warm").Should().BeFalse();

        release.SetResult();
        (await held).Should().Be(new Item("held"));
        await enter;
        readFinishedAtNotification.Should().BeTrue();

        node.IsLocal("held").Should().BeFalse("a late completion from the previous mode never fills the tier");
    }

    [Fact]
    public async Task Entering_bypass_stops_waiting_after_the_source_timeout()
    {
        var node = NewNode(sourceTimeout: TimeSpan.FromSeconds(5));
        Store["held"] = new Item("held");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.LibraryBackend.AfterRead = async () =>
        {
            started.TrySetResult();
            await release.Task;
        };
        var held = node.Cache.TryGetAsync("held", Ct).AsTask();
        await started.Task;
        var enter = node.Router.EnterBypassAsync(Ct);
        await node.Time.WaitForTimerWhileRunningAsync(TimeSpan.FromSeconds(5), enter, Ct);
        enter.IsCompleted.Should().BeFalse();

        Clock.Advance(TimeSpan.FromSeconds(5));
        await enter.WaitAsync(Ct);
        held.IsCompleted.Should().BeFalse("the transition timed out before the read finished");

        node.Router.Mode.Should().Be(RouterMode.BypassLibrary);
        release.SetResult();
        await held;
        node.IsLocal("held").Should().BeFalse();
    }

    [Fact]
    public async Task A_flag_turned_on_and_off_enters_and_leaves_bypass_by_itself()
    {
        var flag = false;
        var node = NewNode(bypass: () => flag);
        Store["k"] = new Item("stored");
        await node.Cache.TryGetAsync("k", Ct);
        node.IsLocal("k").Should().BeTrue();
        var entered = NextMode(node);
        flag = true;

        await node.Cache.TryGetAsync("k", Ct);

        (await entered.WaitAsync(Ct)).Should().Be(RouterMode.BypassLibrary);
        node.Router.ModeReason.Should().Be(RouterModeReason.Flag);
        node.IsLocal("k").Should().BeFalse();
        var left = NextMode(node);
        flag = false;

        await node.Cache.TryGetAsync("k", Ct);

        (await left.WaitAsync(Ct)).Should().Be(RouterMode.Library);
        node.Router.ModeReason.Should().Be(RouterModeReason.Default);
        await node.Cache.TryGetAsync("k", Ct);
        node.IsLocal("k").Should().BeTrue("the tier serves again after the leave");
    }

    [Fact]
    public async Task Leaving_bypass_waits_for_the_subscription_to_be_confirmed()
    {
        var node = NewNode();
        await node.Router.EnterBypassAsync(Ct);
        Bus.Subscribed = false;

        var leave = node.Router.LeaveBypassAsync(Ct);
        await node.Time.WaitForTimerWhileRunningAsync(Poll, leave, Ct);
        Clock.Advance(Poll);
        await node.Time.WaitForTimerWhileRunningAsync(Poll, leave, Ct);

        leave.IsCompleted.Should().BeFalse("peer events cannot be trusted before the subscription is in place");
        node.Router.Mode.Should().Be(RouterMode.BypassLibrary);
        Bus.Subscribed = true;

        Clock.Advance(Poll);
        await leave.WaitAsync(Ct);

        node.Router.Mode.Should().Be(RouterMode.Library);
    }

    [Fact]
    public async Task Leaving_bypass_waits_for_pending_writes_to_settle()
    {
        var node = NewNode(pendingWindow: TimeSpan.FromSeconds(30), sourceTimeout: TimeSpan.FromMinutes(5));
        node.LibraryBackend.WriteOutcome = WriteOutcome.Unknown;
        await node.Cache.SetAsync("k", new Item("new"), default, Ct);
        await node.Router.EnterBypassAsync(Ct);

        var pendingAtNotification = -1;
        node.Router.ModeChanged += (_, _) => pendingAtNotification = node.Cache.PendingKeyCount;
        var leave = node.Router.LeaveBypassAsync(Ct);
        await node.Time.WaitForTimerWhileRunningAsync(node.Core.SourceTimeout, leave, Ct);

        leave.IsCompleted.Should().BeFalse("a write of unknown outcome can still land");
        node.Router.Mode.Should().Be(RouterMode.BypassLibrary);

        await node.Time.WaitForTimerWhileRunningAsync(TimeSpan.FromSeconds(30), leave, Ct);
        Clock.Advance(TimeSpan.FromSeconds(30));
        await leave.WaitAsync(Ct);
        pendingAtNotification.Should().Be(0);

        node.Router.Mode.Should().Be(RouterMode.Library);
    }

    [Fact]
    public async Task Leaving_bypass_takes_the_library_at_once_but_serves_no_shared_tier_hit_until_the_calls_of_the_bypass_have_ended()
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

        var sourceReleasedAtNotification = false;
        node.Router.ModeChanged += (_, _) => sourceReleasedAtNotification = release.Task.IsCompleted;
        var leave = node.Router.LeaveBypassAsync(Ct);
        await node.Time.WaitForTimerWhileRunningAsync(node.Core.SourceTimeout, leave, Ct);

        leave.IsCompleted.Should().BeFalse("a call started in bypass is still running");
        node.Router.Mode.Should().Be(RouterMode.Library, "new calls take the library");
        await node.Cache.TryGetAsync("k", Ct);
        node.LibraryBackend.Reads.Should().Be(1);
        node.IsLocal("k").Should().BeFalse("no fill while the previous mode's calls could still be writing");
        await node.Cache.TryGetAsync("k", Ct);
        node.LibraryBackend.Reads.Should().Be(2, "no shared-tier hit yet");

        release.SetResult();
        await held;
        await leave;
        sourceReleasedAtNotification.Should().BeTrue();
        await node.Cache.TryGetAsync("k", Ct);
        await node.Cache.TryGetAsync("k", Ct);

        node.LibraryBackend.Reads.Should().Be(3);
        node.IsLocal("k").Should().BeTrue();
    }

    [Fact]
    public async Task A_leave_while_the_flag_is_on_changes_nothing()
    {
        var node = NewNode(bypass: () => true);
        await node.Cache.TryGetAsync("k", Ct);
        await node.Router.EnterBypassAsync(Ct);

        await node.Router.LeaveBypassAsync(Ct);

        node.Router.Mode.Should().Be(RouterMode.BypassLibrary);
        node.Router.ModeReason.Should().Be(RouterModeReason.Flag);
    }

    [Fact]
    public async Task A_cancelled_leave_keeps_the_router_in_bypass_with_its_request()
    {
        var node = NewNode();
        await node.Router.EnterBypassAsync(Ct);
        Bus.Subscribed = false;
        using var cancel = new CancellationTokenSource();
        var leave = node.Router.LeaveBypassAsync(cancel.Token);
        await node.Time.WaitForTimerWhileRunningAsync(Poll, leave, Ct);
        leave.IsCompleted.Should().BeFalse();

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leave);
        node.Router.Mode.Should().Be(RouterMode.BypassLibrary);
        node.Router.ModeReason.Should().Be(RouterModeReason.Requested);
    }

    [Fact]
    public async Task A_mode_change_is_reported_once_it_has_drained()
    {
        var node = NewNode();
        var seen = new List<(RouterMode, RouterModeReason)>();
        node.Router.ModeChanged += (mode, reason) => seen.Add((mode, reason));

        await node.Router.EnterBypassAsync(Ct);
        await node.Router.LeaveBypassAsync(Ct);

        seen.Should().Equal((RouterMode.BypassLibrary, RouterModeReason.Requested), (RouterMode.Library, RouterModeReason.Default));
    }

    [Fact]
    public async Task A_write_in_bypass_still_evicts_and_announces_so_peers_on_the_library_stay_coherent()
    {
        var bypassed = NewNode(bypass: () => true);
        var peer = NewNode();
        Store["k"] = new Item("old");
        await peer.Cache.TryGetAsync("k", Ct);
        peer.IsLocal("k").Should().BeTrue();

        await bypassed.Cache.SetAsync("k", new Item("new"), default, Ct);
        await RunBackgroundAsync(bypassed, TimeSpan.FromMilliseconds(100));

        peer.IsLocal("k").Should().BeFalse();
        bypassed.SourceBackend.Writes.Should().Be(1);
    }

    [Fact]
    public async Task A_library_that_fails_to_start_leaves_the_router_in_bypass_and_every_call_on_the_source()
    {
        using var meters = new MeterCollector(CacheName);
        var attempts = 0;
        var node = NewNode(lazyLibrary: () =>
        {
            attempts++;
            throw new InvalidOperationException("stack down");
        });

        var value = await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made")), default, Ct);

        value.Should().Be(new Item("made"));
        node.Router.Mode.Should().Be(RouterMode.BypassLibrary);
        node.Router.ModeReason.Should().Be(RouterModeReason.LibraryUnavailable);
        node.SourceBackend.Reads.Should().Be(1);
        node.SourceBackend.Writes.Should().Be(1);
        node.LibraryBackend.Reads.Should().Be(0);
        attempts.Should().Be(1);
        meters.Count("miss", "path_reason", "library_unavailable").Should().Be(1);
    }

    [Fact]
    public async Task A_library_that_cannot_start_is_retried_with_a_growing_delay_and_the_router_leaves_bypass_once_it_starts()
    {
        var attempts = 0;
        var healthy = false;
        var store = new FakeMigrationCache();
        var node = NewNode(lazyLibrary: () =>
        {
            attempts++;
            return healthy ? Parts(store) : throw new InvalidOperationException("stack down");
        });
        attempts.Should().Be(1);

        var firstDelay = TimeSpan.FromSeconds(1);
        var secondDelay = TimeSpan.FromSeconds(2);
        var thirdDelay = TimeSpan.FromSeconds(4);
        await node.Time.WaitForTimerAsync(firstDelay, Ct);
        Clock.Advance(firstDelay);
        await node.Time.WaitForTimerAsync(secondDelay, Ct);
        attempts.Should().Be(2);
        Clock.Advance(firstDelay);
        attempts.Should().Be(2, "the second delay is two seconds");
        Clock.Advance(firstDelay);
        await node.Time.WaitForTimerAsync(thirdDelay, Ct);
        attempts.Should().Be(3);
        healthy = true;
        var left = NextMode(node);
        Clock.Advance(thirdDelay);

        (await left.WaitAsync(Ct)).Should().Be(RouterMode.Library);
        await node.Cache.TryGetAsync("k", Ct);
        store.Keys.Should().NotBeEmpty("the library path serves now");
    }

    [Fact]
    public async Task With_the_bypass_flag_on_at_start_the_library_is_not_started_until_the_flag_goes_off()
    {
        var flag = true;
        var attempts = 0;
        var store = new FakeMigrationCache();
        var node = NewNode(
            bypass: () => flag,
            lazyLibrary: () =>
            {
                attempts++;
                return Parts(store);
            });
        await node.Time.WaitForTimerAsync(TimeSpan.FromSeconds(1), Ct);
        Clock.Advance(TimeSpan.FromSeconds(1));
        await node.Time.WaitForTimerAsync(TimeSpan.FromSeconds(1), Ct);

        attempts.Should().Be(0, "the flag asks for no library at all");
        node.Router.ModeReason.Should().Be(RouterModeReason.Flag);
        var left = NextMode(node);
        flag = false;
        await node.Cache.TryGetAsync("k", Ct);
        Clock.Advance(TimeSpan.FromSeconds(1));

        (await left.WaitAsync(Ct)).Should().Be(RouterMode.Library);
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Stopping_a_router_that_is_waiting_in_the_background_to_leave_bypass_ends_the_wait()
    {
        var flag = true;
        var node = NewNode(bypass: () => flag);
        var entered = NextMode(node);
        await node.Cache.TryGetAsync("k", Ct);
        await entered.WaitAsync(Ct);
        Bus.Subscribed = false;
        flag = false;
        await node.Cache.TryGetAsync("k", Ct);
        await node.Time.WaitForTimerAsync(Poll, Ct);
        node.Core.IsIdle.Should().BeFalse("the leave is waiting for the subscription");

        await node.StopAsync();

        node.Core.IsIdle.Should().BeTrue();
    }

    [Fact]
    public void The_banner_names_every_setting_and_the_precedence()
    {
        var builder = new RouterBuilder().UseBypassWhen(() => false).UseNodeIdentity("node-1").UseSourceTimeout(TimeSpan.FromSeconds(7)).UseLibrarySettleBound(TimeSpan.FromSeconds(9));

        var banner = RouterBanner.Describe(builder, "node-1");

        banner.Should().Contain("node-1 (UseNodeIdentity)").And.Contain("bypass=UseBypassWhen").And.Contain("source timeout=00:00:07 (UseSourceTimeout)").And.Contain("00:00:09 (UseLibrarySettleBound)").And.Contain("pin=none");
        banner.Should().Contain("pin (UsePinWhen, or a switched group) > bypass (flag, explicit request, library not started) > group predicate");
    }
}
