using Microsoft.Extensions.Logging;
using UiPath.Caching.Broadcast;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.KeyMove.Harness;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The fixes of the review of the first key move PR: each has a test that fails without it.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveReviewTests(RedisFixture fixture)
{
    private static readonly KeyMoveOptions Patient = new() { AdmissionPoll = TimeSpan.FromMilliseconds(100), MaxReruns = 30 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_write_planned_under_an_older_state_is_built_again_under_the_state_in_force()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, options: Patient);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", nPop: 0);

        // The gate and the records already hold the S3 identity; the rules of the node do not, yet.
        await kit.MoveGateAsync();
        var write = kit.Client.SetAsync("k", new Item("v1"), null, Ct).AsTask();
        await Task.Delay(250, Ct);
        write.IsCompleted.Should().BeFalse("the plan of S1 is not run with the identity of S3");
        kit.ApplyRules("S3");

        (await write).Outcome.Should().Be(WriteOutcome.Committed);

        kit.Schedule.Steps.Should().NotContain(SchedulePointStep.SourceSet);
        kit.Schedule.Steps.Should().Contain(SchedulePointStep.Bump);
        (await kit.GetAsync(kit.N("k"))).Should().Be("v1");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task Rules_that_never_catch_up_fail_the_write_closed_and_write_nothing()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, options: new KeyMoveOptions { AdmissionPoll = TimeSpan.FromMilliseconds(5), MaxReruns = 3 });
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", nPop: 0);
        await kit.MoveGateAsync();

        var write = async () => await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        await write.Should().ThrowAsync<CacheUnavailableException>();
        kit.Schedule.Steps.Should().BeEmpty();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retry_that_meets_an_identity_change_is_dropped_and_never_becomes_an_ordinary_write(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "token-before");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        (await kit.Client.SetAsync("k", new Item("old"), null, Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Schedule.Hook = null;
        kit.Connected = true;
        using var meter = new MeterCollector(kit.Cid);

        // Another node's newer write is acknowledged, and then the identity changes.
        await kit.SetStateAsync("S4");
        var other = new FencedKeys(kit.Names.ForwardFence(kit.N("k")), kit.N("k"), kit.Names.ControlFor(kit.Group, kit.N("k")));
        (await kit.Scripts.BumpAsync(other, kit.Id, "other-node", 60_000, BumpOperation.Set, "newer", -1)).Should().BeGreaterThanOrEqualTo(0);

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.GetAsync(kit.N("k"))).Should().Be("newer");
        kit.Client.Retries.Count.Should().Be(0);
        meter.Count("move_retry_superseded").Should().Be(1);
    }

    [Fact]
    public async Task A_promotion_that_cannot_capture_reads_the_union_and_finds_what_another_node_promoted()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Capture && nth == 1)
            {
                // Another node promotes under a newer identity, so this node's CAP is refused and its gate cannot follow.
                await kit.Db.StringSetAsync(kit.N("k"), "v0");
                await kit.Db.KeyDeleteAsync(kit.L("k"));
                await kit.AdvanceRecordsAsync("k", kit.Ep + 1, "later");
            }
        };

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Found, "N holds the value that was continuously present");
        read.Value!.Name.Should().Be("v0");
        read.Uncached.Should().BeTrue();
    }

    [Fact]
    public async Task A_promotion_that_cannot_be_admitted_reads_the_union_and_not_L_alone()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "l-copy");
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.ProbeSource && nth == 1)
            {
                // After the first reads of N, and before the admission: the gate moves on and N appears.
                await kit.Db.StringSetAsync(kit.N("k"), "n-value");
                await kit.MoveGateAsync();
            }
        };

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Value!.Name.Should().Be("n-value", "N outranks L");
    }

    [Fact]
    public async Task The_key_stays_pending_until_its_retry_ends_and_then_for_one_window_more()
    {
        var clock = NewClock();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        await using var node = TestNode.Create(new FakeBus(), clock, [], "kmr" + kit.Unique, library: kit.Client, pendingWindow: TimeSpan.FromSeconds(5));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "token-before");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };

        (await node.Cache.SetAsync("k", new Item("v1"), default, Ct)).Should().Be(WriteOutcome.Unknown);
        node.Cache.PendingKeyCount.Should().Be(1);

        // The settle window passes while the retry is still waiting: the key stays off the local tier.
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(50, Ct);
        }

        node.Cache.PendingKeyCount.Should().Be(1);

        kit.Schedule.Hook = null;
        kit.Connected = true;
        await kit.Client.Retries.RunDueAsync(all: true);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (node.Cache.PendingKeyCount > 0 && DateTime.UtcNow < until)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(20, Ct);
        }

        node.Cache.PendingKeyCount.Should().Be(0);
        (await kit.GetAsync(kit.N("k"))).Should().Be("v1");
    }

    [Fact]
    public async Task Disposal_during_an_outage_with_a_day_long_retry_pending_ends_within_the_bound_and_one_window()
    {
        var clock = NewClock();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        var node = TestNode.Create(new FakeBus(), clock, [], "kmr" + kit.Unique, library: kit.Client, pendingWindow: TimeSpan.FromSeconds(5));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "token-before");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        (await node.Cache.SetAsync("k", new Item("v1"), CacheExpiry.After(TimeSpan.FromDays(1)), Ct)).Should().Be(WriteOutcome.Unknown);
        node.Cache.PendingKeyCount.Should().Be(1);
        kit.Client.Retries.Count.Should().Be(1);

        // The outage lasts: the retry could wait for its day, and disposal must not.
        var disposal = node.Router.DisposeAsync().AsTask();
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!disposal.IsCompleted && DateTime.UtcNow < until)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, Ct);
        }

        disposal.IsCompleted.Should().BeTrue("a retry that will never be sent is not waited for");
        kit.Client.Retries.Count.Should().Be(0);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse("the write stays Unknown, which is allowed");
    }

    [Fact]
    public async Task Disposal_waits_for_a_retry_that_is_running_past_the_settlement_window_and_invalidates_only_after_it_returns()
    {
        var clock = NewClock();
        var bus = new FakeBus();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        var node = TestNode.Create(bus, clock, [], "kmr" + kit.Unique, library: kit.Client, pendingWindow: TimeSpan.FromSeconds(5));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "token-before");
        var gate = new TaskCompletionSource();
        var inRetry = new TaskCompletionSource();
        var bumps = 0;
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                if (Interlocked.Increment(ref bumps) == 1)
                {
                    kit.Connected = false;
                }
                else
                {
                    // The retry's command is in flight and does not return.
                    inRetry.TrySetResult();
                    await gate.Task;
                }
            }
        };
        (await node.Cache.SetAsync("k", new Item("v1"), CacheExpiry.After(TimeSpan.FromDays(1)), Ct)).Should().Be(WriteOutcome.Unknown);
        kit.Connected = true;
        var retry = kit.Client.Retries.RunDueAsync(all: true);
        await inRetry.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        int Removals() => bus.Events.OfType<CacheEvent>().Count(e => e.Key == "k" && e.Type?.Contains("remove", StringComparison.OrdinalIgnoreCase) == true);
        var before = Removals();

        var disposal = node.Router.DisposeAsync().AsTask();
        for (var i = 0; i < 60; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, Ct);
        }

        disposal.IsCompleted.Should().BeFalse("a write is still in flight, and disposal waits for real writes");
        node.Cache.PendingKeyCount.Should().Be(1, "protection lasts until the command has returned and one window has passed");
        Removals().Should().Be(before, "the key is not announced as removed while its command can still land");

        gate.SetResult();
        await retry;
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!disposal.IsCompleted && DateTime.UtcNow < until)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, Ct);
        }

        disposal.IsCompleted.Should().BeTrue();
        Removals().Should().Be(before + 1, "and it is announced once, after the command has returned: " + string.Join(",", bus.Events.OfType<CacheEvent>().Select(e => e.Type)));
    }

    [Fact]
    public async Task A_key_stays_protected_and_is_invalidated_after_a_superseded_running_attempt_returns_even_when_the_newer_write_is_dropped_at_stop()
    {
        var clock = NewClock();
        var bus = new FakeBus();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        var node = TestNode.Create(bus, clock, [], "kmr" + kit.Unique, library: kit.Client, pendingWindow: TimeSpan.FromSeconds(5));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "token-before");
        var gate = new TaskCompletionSource();
        var inAttempt = new TaskCompletionSource();
        var bumps = 0;
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step != SchedulePointStep.Bump)
            {
                return;
            }

            switch (Interlocked.Increment(ref bumps))
            {
                case 1:
                case 3:
                    // The first write and the newer one both fail their trailing step.
                    kit.Connected = false;
                    break;
                case 2:
                    // Attempt A: its command is in flight and does not return.
                    inAttempt.TrySetResult();
                    await gate.Task;
                    break;
            }
        };
        // The pending waiter of the key is held back until the test lets it look at the key's hold, so it has not captured A's when B registers.
        var waiterGate = new TaskCompletionSource();
        node.Cache.Pending.BeforeWaiterLooks = _ => waiterGate.Task;
        (await node.Cache.SetAsync("k", new Item("v1"), CacheExpiry.After(TimeSpan.FromDays(1)), Ct)).Should().Be(WriteOutcome.Unknown);
        kit.Connected = true;
        var attempt = kit.Client.Retries.RunDueAsync(all: true);
        await inAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // A newer write supersedes A while A runs, and its own retry (B) registers a hold of its own.
        (await node.Cache.SetAsync("k", new Item("v2"), CacheExpiry.After(TimeSpan.FromDays(1)), Ct)).Should().Be(WriteOutcome.Unknown);
        kit.Connected = true;
        int Removals() => bus.Events.OfType<CacheEvent>().Count(e => e.Key == "k" && e.Type?.Contains("remove", StringComparison.OrdinalIgnoreCase) == true);
        var before = Removals();

        // The stop drops the waiting B; A has not returned.
        var disposal = node.Router.DisposeAsync().AsTask();
        for (var i = 0; i < 80; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15, Ct);
        }

        waiterGate.SetResult();
        await Task.Delay(100, Ct);

        // Longer than the settle window the library reports, so a waiter that lost A's hold would retire the key in this time.
        for (var i = 0; i < 60; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15, Ct);
        }

        disposal.IsCompleted.Should().BeFalse();
        node.Cache.PendingKeyCount.Should().Be(1, "A can still write, so the key is still protected");
        Removals().Should().Be(before, "no removal is announced before A returns");

        gate.SetResult();
        await attempt;
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!disposal.IsCompleted && DateTime.UtcNow < until)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15, Ct);
        }

        disposal.IsCompleted.Should().BeTrue();
        Removals().Should().Be(before + 1, "and the key is announced removed once A has returned");
    }

    [Fact]
    public async Task The_foreground_stop_returns_within_its_bound_and_warns_while_disposal_waits_for_a_write_that_does_not_return()
    {
        var clock = NewClock();
        var logger = new RecordingLogger();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        var node = TestNode.Create(new FakeBus(), clock, [], "kmr" + kit.Unique, library: kit.Client, pendingWindow: TimeSpan.FromSeconds(5), logger: logger);
        await kit.PrepareAsync("hang-key");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("hang-key")), "token-before");
        var gate = new TaskCompletionSource();
        var inAttempt = new TaskCompletionSource();
        var bumps = 0;
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                if (Interlocked.Increment(ref bumps) == 1)
                {
                    kit.Connected = false;
                }
                else
                {
                    inAttempt.TrySetResult();
                    await gate.Task;
                }
            }
        };
        (await node.Cache.SetAsync("hang-key", new Item("v1"), CacheExpiry.After(TimeSpan.FromDays(1)), Ct)).Should().Be(WriteOutcome.Unknown);
        kit.Connected = true;
        var attempt = kit.Client.Retries.RunDueAsync(all: true);
        await inAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var stop = node.Router.StopAsync(CancellationToken.None);
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!stop.IsCompleted && DateTime.UtcNow < until)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15, Ct);
        }

        stop.IsCompleted.Should().BeTrue("the foreground stop is released at its bound whatever an attempt does");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("hang-key", StringComparison.Ordinal), "the stop names the key still in flight");

        var disposal = node.Router.DisposeAsync().AsTask();
        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15, Ct);
        }

        disposal.IsCompleted.Should().BeFalse("disposal waits for a write that is really in flight, however long it takes");
        gate.SetResult();
        await attempt;
        until = DateTime.UtcNow.AddSeconds(20);
        while (!disposal.IsCompleted && DateTime.UtcNow < until)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15, Ct);
        }

        disposal.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task The_foreground_stop_is_released_at_one_shared_bound_when_the_settlement_and_the_invalidation_both_stall()
    {
        var clock = NewClock();
        var bus = new FakeBus();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        var node = TestNode.Create(bus, clock, [], "kmr" + kit.Unique, library: kit.Client, pendingWindow: TimeSpan.FromSeconds(5));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "token-before");
        var gate = new TaskCompletionSource();
        var inAttempt = new TaskCompletionSource();
        var bumps = 0;
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                if (Interlocked.Increment(ref bumps) == 1)
                {
                    kit.Connected = false;
                }
                else
                {
                    inAttempt.TrySetResult();
                    await gate.Task;
                }
            }
        };
        (await node.Cache.SetAsync("k", new Item("v1"), CacheExpiry.After(TimeSpan.FromDays(1)), Ct)).Should().Be(WriteOutcome.Unknown);
        kit.Connected = true;
        var attempt = kit.Client.Retries.RunDueAsync(all: true);
        await inAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // The pending key cannot settle, and the cache-level invalidation cannot be published.
        var hold = new TaskCompletionSource();
        bus.Hold = hold;
        var began = clock.GetUtcNow();
        var stop = node.Router.StopAsync(CancellationToken.None);
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!stop.IsCompleted && DateTime.UtcNow < until)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            // Give a stop that is now due the chance to complete before the clock moves again, so at most one step overshoots.
            await Task.WhenAny(stop, Task.Delay(200, Ct));
        }

        stop.IsCompleted.Should().BeTrue();
        (clock.GetUtcNow() - began).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(6), "one bound of 5 seconds for the whole stop, not 5 for the settlement and 5 more for the invalidation; one poll step of slack");

        hold.SetResult();
        gate.SetResult();
        await attempt;
        until = DateTime.UtcNow.AddSeconds(30);
        var disposal = node.Router.DisposeAsync().AsTask();
        while (!disposal.IsCompleted && DateTime.UtcNow < until)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15, Ct);
        }

        disposal.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task A_losing_fill_keeps_the_winner_locally_only_for_what_is_left_of_its_life()
    {
        var clock = NewClock();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        await using var node = TestNode.Create(new FakeBus(), clock, [], "kmr" + kit.Unique, library: kit.Client, localCap: TimeSpan.FromMinutes(1));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");

        var got = await node.Cache.GetOrAddAsync(
            "k",
            async _ =>
            {
                await kit.Db.StringSetAsync(kit.N("k"), "winner", TimeSpan.FromSeconds(2));
                return new Item("generated");
            },
            default,
            Ct);

        got!.Name.Should().Be("winner");
        node.IsLocal("k").Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(3));
        node.IsLocal("k").Should().BeFalse("the winner had two seconds left, not the cap");
    }

    [Fact]
    public async Task The_invariant_runner_flags_an_unavailable_read_in_a_scenario_with_no_fault()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        var violations = new List<string>();
        var context = new InterleavingContext(kit, new HistoryOracle([new OracleKey("k", false, "v0")]), violations);
        kit.Connected = false;

        await context.Read()();
        await context.Write("v1")();

        violations.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_retry_asks_for_what_is_left_of_the_deadline_and_not_the_original_life_again()
    {
        var clock = NewClock();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "token-before");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        (await kit.Client.SetAsync("k", new Item("v1"), clock.GetUtcNow().AddSeconds(100), Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Schedule.Hook = null;
        kit.Connected = true;

        clock.Advance(TimeSpan.FromSeconds(60));
        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.GetAsync(kit.N("k"))).Should().Be("v1");
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task A_retry_whose_deadline_has_passed_is_dropped_and_a_dead_deadline_delete_is_not_exhausted_at_once()
    {
        var clock = NewClock();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "token-before");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        (await kit.Client.SetAsync("k", new Item("v1"), clock.GetUtcNow().AddSeconds(10), Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Schedule.Hook = null;
        kit.Connected = true;
        clock.Advance(TimeSpan.FromSeconds(11));

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        kit.Plane.Alerts.Should().ContainSingle(a => a.Code == "retry_exhausted", "a retry that outlived its deadline gave up");

        // A deadline already dead is a delete, with no life to hold its retry to.
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        await kit.Client.SetAsync("k", new Item("v2"), clock.GetUtcNow().AddSeconds(-1), Ct);
        kit.Plane.Alerts.Should().ContainSingle("a delete has no life to run out, so it is held, not given up at once");
        kit.Client.Retries.IsPending("k").Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_NoOrigin_delete_removes_L_before_N_and_a_reader_between_never_finds_the_older_value(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        await kit.Db.StringSetAsync(kit.N("k"), "v0");
        string? between = "unset";
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                between = (await kit.Client.ReadAsync("k", Ct)).Value?.Name;
            }
        };

        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        kit.Schedule.Steps.Take(2).Should().Equal(SchedulePointStep.SourceDelete, SchedulePointStep.FenceObserve);
        kit.Schedule.Steps.Should().Contain(SchedulePointStep.Bump);
        between.Should().Be("v0", "a reader still finds the value on N after L is deleted");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BUMPIF_deletes_only_over_the_nonce_or_the_observed_token(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        var fence = kit.Names.ForwardFence(kit.N("k"));
        var keys = new FencedKeys(fence, kit.N("k"), kit.Names.ControlFor(kit.Group, kit.N("k")));

        await kit.Db.StringSetAsync(kit.N("k"), "v");
        await kit.Db.StringSetAsync(fence, "A");
        (await kit.Scripts.BumpIfFenceAsync(keys, kit.Id, "A", "B", 60_000, BumpOperation.Delete)).Should().Be(1);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.FenceAsync("k")).Should().Be("A");

        await kit.Db.StringSetAsync(kit.N("k"), "v");
        await kit.Db.StringSetAsync(fence, "B");
        (await kit.Scripts.BumpIfFenceAsync(keys, kit.Id, "A", "B", 60_000, BumpOperation.Delete)).Should().Be(1);

        await kit.Db.StringSetAsync(kit.N("k"), "newer");
        await kit.Db.StringSetAsync(fence, "newer-token");
        (await kit.Scripts.BumpIfFenceAsync(keys, kit.Id, "A", "B", 60_000, BumpOperation.Delete)).Should().Be(MoveScripts.Superseded);
        await kit.Db.KeyDeleteAsync(fence);
        (await kit.Scripts.BumpIfFenceAsync(keys, kit.Id, "A", null, 60_000, BumpOperation.Delete)).Should().Be(MoveScripts.Superseded);
        (await kit.GetAsync(kit.N("k"))).Should().Be("newer");
        (await kit.Scripts.BumpIfFenceAsync(keys, "stale", "A", "B", 60_000, BumpOperation.Delete)).Should().Be(MoveScripts.IdentityMismatch);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_retried_delete_of_a_key_another_node_has_since_rewritten_is_dropped(bool scripts, bool noOrigin)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        await kit.Db.StringSetAsync(kit.N("k"), "v0");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "token-before");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Schedule.Hook = null;
        kit.Connected = true;
        var other = new FencedKeys(kit.Names.ForwardFence(kit.N("k")), kit.N("k"), kit.Names.ControlFor(kit.Group, kit.N("k")));
        await kit.Scripts.BumpAsync(other, kit.Id, "other-node", 60_000, BumpOperation.Set, "acknowledged", -1);

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.GetAsync(kit.N("k"))).Should().Be("acknowledged");
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("same")]
    [InlineData("different")]
    public async Task A_NoOrigin_miss_in_S1_with_an_absent_control_record_reads_the_union_and_does_not_answer_unchanged(string record)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", nPop: 0);
        await kit.Db.StringSetAsync(kit.N("k"), "moved");
        if (record == "absent")
        {
            await kit.Db.KeyDeleteAsync(kit.Names.ControlFor(kit.Group, kit.L("k")));
        }
        else if (record == "different")
        {
            kit.Schedule.Hook = async (step, nth) =>
            {
                if (step == SchedulePointStep.ControlCheck && nth == 1)
                {
                    await kit.SetStateAsync("S3");
                }
            };
        }

        var read = await kit.Client.ReadAsync("k", Ct);

        // Same: a miss, because the identity is the one the unit runs under. Absent and different: the key may have moved, so N is found.
        read.Kind.Should().Be(record == "same" ? ReadOutcome.Absent : ReadOutcome.Found);
    }

    private static ManualTimeProvider NewClock() => new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
}
