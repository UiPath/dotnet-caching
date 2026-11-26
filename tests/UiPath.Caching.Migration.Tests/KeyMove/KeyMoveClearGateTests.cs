using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A Clear of one cache over the real gate protocol, the real N sweep and a real client. Outcome: no value older than a completed Clear is readable afterwards, and a failure reopens only this Clear's gates.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveClearGateTests(RedisFixture fixture)
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_promotion_in_flight_across_a_clear_serves_its_caller_but_publishes_nothing()
    {
        await using var ck = await ClearKit.CreateAsync(fixture, "S3");
        var kit = ck.Kit;
        await kit.Db.StringSetAsync(kit.L("k"), "old", Life);
        var events = new List<string>();
        var clear = ClearOf(ck, events);
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Publish && nth == 1)
            {
                await clear.ClearAsync(Ct);
            }
        };

        var inFlight = await kit.Client.ReadAsync("k", Ct);

        events.Should().Equal("local", "publish", "source", "local", "publish");
        inFlight.Kind.Should().Be(ReadOutcome.Found, "the read overlapped the Clear and may serve what it read");
        inFlight.Uncached.Should().BeTrue("a refused promotion is served and never kept");
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse("the promotion's publish was refused");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        kit.Schedule.Hook = null;
        (await kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Absent, "nothing older than the completed Clear is readable");
        (await ck.GateAsync()).Should().StartWith("OPEN:");
    }

    [Fact]
    public async Task A_promoter_that_captured_before_the_gate_closed_is_refused_at_its_publish_during_the_clear_and_after_it()
    {
        await using var ck = await ClearKit.CreateAsync(fixture, "S3");
        var kit = ck.Kit;
        var during = await CapturedAsync(ck, "during");
        var after = await CapturedAsync(ck, "after");
        long? duringCode = null;
        var clear = ClearOf(ck, [], async () => duringCode = await PublishAsync(ck, during));

        await clear.ClearAsync(Ct);
        var afterCode = await PublishAsync(ck, after);

        duringCode.Should().Be(MoveScripts.GateRefused, "the gate is closed while the Clear sweeps");
        afterCode.Should().Be(MoveScripts.GateRefused, "the reopened gate carries a new nonce, which no earlier capture equals");
        (await kit.ExistsAsync(kit.N("during"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.N("after"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_node_that_never_heard_of_the_clear_cannot_promote_during_it_and_counts_the_closed_gate()
    {
        await using var ck = await ClearKit.CreateAsync(fixture, "S3");
        var kit = ck.Kit;
        using var meter = new MeterCollector(kit.Cid);
        await kit.Db.StringSetAsync(kit.L("k"), "old", Life);
        ReadOutcome? during = null;

        // The events of the Clear are never delivered to this client: it holds the rules and the hints it had before.
        var clear = ClearOf(ck, [], async () => during = (await kit.Client.ReadAsync("k", Ct)).Kind);
        await clear.ClearAsync(Ct);

        during.Should().Be(ReadOutcome.Absent, "the capture is refused on the closed gate: a miss with no hint");
        meter.Count("move_gate_closed").Should().Be(1);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse("the old L value was never promoted");
        (await kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Absent);
    }

    [Fact]
    public async Task A_capture_refused_on_a_closed_or_absent_gate_counts_gate_closed_and_misses_and_the_next_open_gate_promotes()
    {
        await using var ck = await ClearKit.CreateAsync(fixture, "S3");
        var kit = ck.Kit;
        using var meter = new MeterCollector(kit.Cid);
        await kit.Db.StringSetAsync(kit.L("k"), "v1", Life);
        var control = kit.Names.Control(kit.Group, 0);

        (await kit.Scripts.GateAsync(control, kit.Id, kit.Cid, "CLOSED:manual")).Should().Be(MoveScripts.Written);
        (await kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Absent);
        await kit.Db.HashDeleteAsync(control, "c:" + kit.Cid);
        (await kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Absent, "an absent gate refuses as well");
        meter.Count("move_gate_closed").Should().Be(2);

        (await kit.Scripts.GateAsync(control, kit.Id, kit.Cid, "OPEN:again")).Should().Be(MoveScripts.Written);
        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v1");
        meter.Count("move_gate_closed").Should().Be(2);
    }

    [Fact]
    public async Task The_source_owned_clear_runs_under_a_closed_gate_and_the_sweep_takes_the_cache_s_N_keys_fences_and_staging_and_no_other_cache_s()
    {
        await using var ck = await ClearKit.CreateAsync(fixture, "S3");
        var kit = ck.Kit;
        var mine = new List<RedisKey>();
        foreach (var n in Enumerable.Range(0, 20))
        {
            mine.Add(kit.N("k" + n));
            mine.Add(kit.Names.ForwardFence(kit.N("k" + n)));
        }

        mine.Add(kit.Names.ForwardStaging(kit.N("k0"), MoveScripts.NewNonce()));
        foreach (var key in mine.Concat(Enumerable.Range(0, 5).Select(n => (RedisKey)kit.L("k" + n))))
        {
            await kit.Db.StringSetAsync(key, "v", Life);
        }

        var other = $"km4:{kit.Unique}:Nx:k";
        await kit.Db.StringSetAsync(other, "v", Life);
        string? gateDuringSourceClear = null;
        string? lockDuringSourceClear = null;
        var events = new List<string>();
        var clear = ClearOf(
            ck,
            events,
            async () =>
            {
                gateDuringSourceClear = await ck.GateAsync();
                lockDuringSourceClear = await kit.Db.StringGetAsync(kit.Names.ClearLock(kit.Group, kit.Cid));
            });

        await clear.ClearAsync(Ct);

        events.Should().Equal("local", "publish", "source", "local", "publish");
        gateDuringSourceClear.Should().StartWith("CLOSED:");
        lockDuringSourceClear.Should().NotBeNullOrEmpty();
        foreach (var key in mine)
        {
            (await kit.Db.KeyExistsAsync(key)).Should().BeFalse(key.ToString());
        }

        foreach (var n in Enumerable.Range(0, 5))
        {
            (await kit.ExistsAsync(kit.L("k" + n))).Should().BeFalse("the source's own clear took L");
        }

        (await kit.Db.KeyExistsAsync(other)).Should().BeTrue("another cache's key stays");
        (await ck.GateAsync()).Should().StartWith("OPEN:");
        (await kit.Db.KeyExistsAsync(kit.Names.ClearLock(kit.Group, kit.Cid))).Should().BeFalse();
        await kit.Db.KeyDeleteAsync(other);
    }

    [Fact]
    public async Task A_gate_replaced_on_every_try_throws_unavailable_and_leaves_an_open_gate_and_no_lock()
    {
        await using var ck = await ClearKit.CreateAsync(fixture, "S3");
        var kit = ck.Kit;
        var clear = ClearOf(ck, [], () => kit.Db.HashSetAsync(kit.Names.Control(kit.Group, 0), "c:" + kit.Cid, "OPEN:rebuilt" + Guid.NewGuid().ToString("N")));

        var act = () => clear.ClearAsync(Ct);

        await act.Should().ThrowAsync<CacheUnavailableException>().WithMessage("*could not be made safe*");
        (await ck.GateAsync()).Should().StartWith("OPEN:");
        (await kit.Db.KeyExistsAsync(kit.Names.ClearLock(kit.Group, kit.Cid))).Should().BeFalse();
    }

    [Fact]
    public async Task While_the_state_is_not_durable_the_clear_is_unavailable_runs_nothing_and_is_retried_until_it_clears()
    {
        await using var ck = await ClearKit.CreateAsync(fixture, "S3");
        var kit = ck.Kit;
        await kit.Db.HashSetAsync(kit.Names.State(kit.Group), StateFields.Durable, "0");
        await kit.Db.StringSetAsync(kit.N("k"), "stale", Life);
        var sweeps = 0;
        var gatedCalls = 0;
        var clear = new KeyMoveClear(
            kit.Group,
            kit.Cid,
            async (group, cache, hooks, token) =>
            {
                gatedCalls++;
                var outcome = await ck.Coordinator.ClearAsync(group, cache, hooks, token);
                if (outcome == ClearOutcome.Unavailable)
                {
                    await kit.Db.HashSetAsync(kit.Names.State(kit.Group), StateFields.Durable, "1");
                }

                return outcome;
            },
            ck.Sweep.SweepAsync,
            _ =>
            {
                sweeps++;
                return ValueTask.CompletedTask;
            },
            () => { },
            () => ValueTask.CompletedTask,
            ck.Options.ClearRetries,
            ck.RetryTime,
            TimeSpan.FromMilliseconds(10));

        var running = clear.ClearAsync(Ct);
        await ck.RetryTime.AdvanceNextTimerAsync(Ct);
        await running;

        gatedCalls.Should().Be(2);
        sweeps.Should().Be(1, "nothing ran while the state was not durable");
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_state_that_stays_not_durable_throws_unavailable_after_the_attempts_and_closes_no_gate()
    {
        await using var ck = await ClearKit.CreateAsync(fixture, "S3");
        var kit = ck.Kit;
        await kit.Db.HashSetAsync(kit.Names.State(kit.Group), StateFields.Durable, "0");
        var ran = false;
        ValueTask SourceClear(CancellationToken _)
        {
            ran = true;
            return ValueTask.CompletedTask;
        }

        var clear = new KeyMoveClear(kit.Group, kit.Cid, ck.Coordinator.ClearAsync, ck.Sweep.SweepAsync, SourceClear, () => { }, () => ValueTask.CompletedTask, 2, ck.RetryTime, TimeSpan.FromMilliseconds(5));

        var act = clear.ClearAsync(Ct);
        await ck.RetryTime.AdvanceNextTimerAsync(Ct);

        await Assert.ThrowsAsync<CacheUnavailableException>(() => act);
        ran.Should().BeFalse();
        (await ck.GateAsync()).Should().StartWith("OPEN:");
    }

    [Fact]
    public async Task Concurrent_clears_of_one_cache_one_wins_and_the_other_is_unavailable_and_retried_until_the_first_ends()
    {
        await using var ck = await ClearKit.CreateAsync(fixture, "S3");
        var kit = ck.Kit;
        var inSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweeps = 0;
        var unavailable = 0;
        var first = new KeyMoveClear(
            kit.Group,
            kit.Cid,
            ck.Coordinator.ClearAsync,
            ck.Sweep.SweepAsync,
            async token =>
            {
                Interlocked.Increment(ref sweeps);
                inSweep.TrySetResult();
                await release.Task.WaitAsync(token);
            },
            () => { },
            () => ValueTask.CompletedTask,
            ck.Options.ClearRetries,
            ck.RetryTime);
        var second = new KeyMoveClear(
            kit.Group,
            kit.Cid,
            async (group, cache, hooks, token) =>
            {
                var outcome = await ck.Coordinator.ClearAsync(group, cache, hooks, token);
                if (outcome == ClearOutcome.Unavailable)
                {
                    Interlocked.Increment(ref unavailable);
                }

                return outcome;
            },
            ck.Sweep.SweepAsync,
            _ =>
            {
                Interlocked.Increment(ref sweeps);
                return ValueTask.CompletedTask;
            },
            () => { },
            () => ValueTask.CompletedTask,
            1000,
            ck.RetryTime,
            TimeSpan.FromMilliseconds(5));

        var winning = first.ClearAsync(Ct);
        await inSweep.Task.WaitAsync(Ct);
        var waiting = second.ClearAsync(Ct);
        await ck.RetryTime.AdvanceNextTimerAsync(Ct);
        var nextAttempt = await ck.RetryTime.NextTimerAsync(Ct);
        Volatile.Read(ref unavailable).Should().Be(2);

        (await ck.GateAsync()).Should().StartWith("CLOSED:", "the second Clear never reopens the first one's gate");
        Volatile.Read(ref sweeps).Should().Be(1, "the second Clear swept nothing while the first held the lock");
        release.SetResult();
        await winning;
        ck.RetryTime.Advance(nextAttempt);
        await waiting;

        Volatile.Read(ref sweeps).Should().Be(2);
        (await ck.GateAsync()).Should().StartWith("OPEN:");
        (await kit.Db.KeyExistsAsync(kit.Names.ClearLock(kit.Group, kit.Cid))).Should().BeFalse();
    }

    [Fact]
    public async Task A_clear_that_stays_unavailable_throws_without_touching_the_gate_of_the_clear_that_holds_the_lock()
    {
        await using var ck = await ClearKit.CreateAsync(fixture, "S3");
        var kit = ck.Kit;
        var inSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask Hold(CancellationToken token)
        {
            inSweep.TrySetResult();
            await release.Task.WaitAsync(token);
        }

        var holder = new KeyMoveClear(kit.Group, kit.Cid, ck.Coordinator.ClearAsync, ck.Sweep.SweepAsync, Hold, () => { }, () => ValueTask.CompletedTask, 3, ck.RetryTime);
        var loser = new KeyMoveClear(kit.Group, kit.Cid, ck.Coordinator.ClearAsync, ck.Sweep.SweepAsync, _ => ValueTask.CompletedTask, () => { }, () => ValueTask.CompletedTask, 2, ck.RetryTime, TimeSpan.FromMilliseconds(5));

        var holding = holder.ClearAsync(Ct);
        await inSweep.Task.WaitAsync(Ct);
        var act = loser.ClearAsync(Ct);
        await ck.RetryTime.AdvanceNextTimerAsync(Ct);

        await Assert.ThrowsAsync<CacheUnavailableException>(() => act);
        (await ck.GateAsync()).Should().StartWith("CLOSED:");
        release.SetResult();
        await holding;
        (await ck.GateAsync()).Should().StartWith("OPEN:");
    }

    [Fact]
    public async Task A_create_during_a_clear_is_refused_and_counted_and_a_create_that_captured_before_it_cannot_publish()
    {
        await using var ck = await ClearKit.CreateAsync(fixture, "S3", noOrigin: true);
        var kit = ck.Kit;
        using var meter = new MeterCollector(kit.Cid);
        TryAddKind? during = null;
        var clear = ClearOf(ck, [], async () => during = (await kit.Client.TryAddAsync("during", new Item("claim"), Life, Ct)).Kind);

        await clear.ClearAsync(Ct);

        during.Should().Be(TryAddKind.Failed);
        meter.Count("move_gate_closed").Should().Be(1);
        (await kit.ExistsAsync(kit.N("during"))).Should().BeFalse();

        var inFlight = ClearOf(ck, []);
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Publish && nth == 1)
            {
                await inFlight.ClearAsync(Ct);
            }
        };
        var captured = await kit.Client.TryAddAsync("captured", new Item("claim"), Life, Ct);
        kit.Schedule.Hook = null;

        captured.Kind.Should().Be(TryAddKind.Failed, "its publish found a gate that is not the one it captured");
        (await kit.ExistsAsync(kit.N("captured"))).Should().BeFalse();
        (await kit.Client.TryAddAsync("captured", new Item("claim-2"), Life, Ct)).Kind.Should().Be(TryAddKind.Created, "once the Clear is done a create works");
        (await kit.GetAsync(kit.N("captured"))).Should().Be("claim-2");
    }

    [Fact]
    public async Task A_key_that_two_caches_own_makes_the_clear_unavailable_with_the_reason_and_leaves_the_key_and_an_open_gate()
    {
        await using var ck = await ClearKit.CreateAsync(fixture, "S3");
        var kit = ck.Kit;
        var rival = ck.Cache with { Name = "rival" };
        var sweep = ck.SweepWith(ck.Cache, rival);
        await kit.Db.StringSetAsync(kit.N("k"), "v", Life);
        var clear = new KeyMoveClear(kit.Group, kit.Cid, ck.Coordinator.ClearAsync, sweep.SweepAsync, _ => ValueTask.CompletedTask, () => { }, () => ValueTask.CompletedTask, 1, ck.RetryTime, TimeSpan.Zero);

        var act = () => clear.ClearAsync(Ct);

        await act.Should().ThrowAsync<CacheUnavailableException>().WithMessage("*owned by*rival*");
        (await kit.ExistsAsync(kit.N("k"))).Should().BeTrue();
        (await ck.GateAsync()).Should().StartWith("OPEN:");
    }

    // The source's own Clear stands for the application's: it takes every L key of the cache, after what the test wants to see while the gate is closed.
    private static KeyMoveClear ClearOf(ClearKit ck, List<string> events, Func<Task>? whileClosed = null)
    {
        var kit = ck.Kit;
        return new KeyMoveClear(
            kit.Group,
            kit.Cid,
            ck.Coordinator.ClearAsync,
            ck.Sweep.SweepAsync,
            async token =>
            {
                events.Add("source");
                if (whileClosed is not null)
                {
                    await whileClosed();
                }

                foreach (var server in kit.Mux.GetServers().Where(s => !s.IsReplica))
                {
                    foreach (var key in server.Keys(pattern: $"km4:{kit.Unique}:L:*").ToArray())
                    {
                        token.ThrowIfCancellationRequested();
                        await kit.Db.KeyDeleteAsync(key);
                    }
                }
            },
            () => events.Add("local"),
            () =>
            {
                events.Add("publish");
                return ValueTask.CompletedTask;
            },
            ck.Options.ClearRetries,
            ck.RetryTime,
            TimeSpan.FromMilliseconds(10));
    }

    private static async Task<(string Key, Capture Capture)> CapturedAsync(ClearKit ck, string key)
    {
        var kit = ck.Kit;
        await kit.Db.StringSetAsync(kit.L(key), "old", Life);
        var n = kit.N(key);
        var capture = await kit.Scripts.CaptureAsync(new FencedKeys(kit.Names.ForwardFence(n), n, kit.Names.ControlFor(kit.Group, n)), kit.Id, kit.Cid, MoveScripts.NewNonce(), 60_000);
        capture.Captured.Should().BeTrue();
        return (key, capture);
    }

    private static async Task<long> PublishAsync(ClearKit ck, (string Key, Capture Capture) captured)
    {
        var kit = ck.Kit;
        var n = kit.N(captured.Key);
        var publication = new Publication(captured.Capture.Token!, captured.Capture.Gate!, kit.Cid, MoveScripts.NewNonce(), 60_000, MoveScripts.NoExpiry, MoveScripts.NoExpiry, true);
        return await kit.Scripts.PublishAsync(new FencedKeys(kit.Names.ForwardFence(n), n, kit.Names.ControlFor(kit.Group, n)), kit.Id, "old", publication);
    }
}
