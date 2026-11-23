using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class ClearCoordinatorTests(RedisContainerFixture fixture)
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task A_clear_closes_only_its_cache_during_the_sweep_and_reopens_it_after_the_second_event()
    {
        await using var kit = await StartedAsync();
        var order = new List<string>();
        var duringSweep = new List<string?>();
        var hooks = new ClearHooks(
            _ => Task.Run(() => order.Add("first")),
            async _ =>
            {
                order.Add("sweep");
                duringSweep.Add(await Gate(kit, "a"));
                duringSweep.Add(await Gate(kit, "b"));
                duringSweep.Add(await kit.Db.StringGetAsync(kit.Names.ClearLock(kit.Group, "a")));
            },
            _ => Task.Run(() => order.Add("second")));

        (await Coordinator(kit).ClearAsync(kit.Group, "a", hooks, Ct)).Should().Be(ClearOutcome.Cleared);

        order.Should().Equal("first", "sweep", "second");
        duringSweep[0].Should().StartWith("CLOSED:");
        duringSweep[1].Should().StartWith("OPEN:");
        duringSweep[2].Should().NotBeNullOrEmpty("the lock is held during the sweep");
        (await Gate(kit, "a")).Should().StartWith("OPEN:");
        (await kit.Db.KeyExistsAsync(kit.Names.ClearLock(kit.Group, "a"))).Should().BeFalse("the lock is released");
    }

    [Fact]
    public async Task The_lock_key_carries_the_library_prefix_and_the_group_tag()
    {
        await using var kit = await StartedAsync();

        kit.Names.ClearLock(kit.Group, "a").ToString().Should().Be($"{StateKit.Library}mg:{{{kit.Group}}}:clrlock:a");
    }

    [Fact]
    public async Task A_group_in_S0_or_with_no_state_gets_the_plain_sweep_and_no_gate_is_written()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var calls = new List<string>();
        var hooks = new ClearHooks(_ => Task.Run(() => calls.Add("first")), _ => Task.Run(() => calls.Add("sweep")), _ => Task.Run(() => calls.Add("second")));

        (await Coordinator(kit).ClearAsync(kit.Group, "a", hooks, Ct)).Should().Be(ClearOutcome.Cleared, "no state hash");
        await kit.InitAsync("a");
        (await Coordinator(kit).ClearAsync(kit.Group, "a", hooks, Ct)).Should().Be(ClearOutcome.Cleared, "S0");

        calls.Should().Equal("first", "sweep", "second", "first", "sweep", "second");
        (await kit.ControlDb.KeyExistsAsync(kit.Names.Control(kit.Group, 0))).Should().BeFalse();
    }

    [Fact]
    public async Task A_transition_in_flight_makes_the_clear_unavailable_and_runs_nothing()
    {
        await using var kit = await StartedAsync();
        var stored = (await kit.Store.ReadAsync(kit.Group, Ct))!;
        await kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(stored), StateKit.TargetFrom(stored, "S2", "fwd", 0));
        var ran = false;

        var outcome = await Coordinator(kit).ClearAsync(kit.Group, "a", Hooks(() => ran = true), Ct);

        outcome.Should().Be(ClearOutcome.Unavailable);
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task Another_clear_of_the_cache_holds_the_lock_so_this_one_is_unavailable_and_closes_nothing()
    {
        await using var kit = await StartedAsync();
        await kit.Db.StringSetAsync(kit.Names.ClearLock(kit.Group, "a"), "other", TimeSpan.FromMinutes(1));
        var ran = false;

        (await Coordinator(kit).ClearAsync(kit.Group, "a", Hooks(() => ran = true), Ct)).Should().Be(ClearOutcome.Unavailable);

        ran.Should().BeFalse();
        (await Gate(kit, "a")).Should().StartWith("OPEN:");
        (await kit.Db.StringGetAsync(kit.Names.ClearLock(kit.Group, "a"))).Should().Be("other", "the other Clear's lock is not released by this one");
        await kit.Db.KeyDeleteAsync(kit.Names.ClearLock(kit.Group, "a"));
    }

    [Fact]
    public async Task A_gate_replaced_during_the_sweep_makes_the_clear_close_and_sweep_again()
    {
        await using var kit = await StartedAsync();
        var sweeps = 0;
        var hooks = new ClearHooks(
            _ => Task.CompletedTask,
            async _ =>
            {
                // A rebuilt record carries a fresh open gate, which the check after the sweep must notice.
                if (++sweeps == 1)
                {
                    await kit.ControlDb.HashSetAsync(kit.Names.Control(kit.Group, 0), "c:a", "OPEN:rebuilt");
                }
            },
            _ => Task.CompletedTask);

        (await Coordinator(kit).ClearAsync(kit.Group, "a", hooks, Ct)).Should().Be(ClearOutcome.Cleared);

        sweeps.Should().Be(2);
        (await Gate(kit, "a")).Should().StartWith("OPEN:").And.NotBe("OPEN:rebuilt");
    }

    [Fact]
    public async Task A_gate_that_is_replaced_on_every_sweep_fails_the_clear_after_the_retries_and_leaves_no_closed_gate_and_no_lock()
    {
        await using var kit = await StartedAsync();
        var sweeps = 0;
        var hooks = new ClearHooks(
            _ => Task.CompletedTask,
            async _ =>
            {
                sweeps++;
                await kit.ControlDb.HashSetAsync(kit.Names.Control(kit.Group, 0), "c:a", "OPEN:rebuilt" + sweeps);
            },
            _ => Task.CompletedTask);

        (await Coordinator(kit).ClearAsync(kit.Group, "a", hooks, Ct)).Should().Be(ClearOutcome.Failed);

        sweeps.Should().Be(kit.Options.ClearRetries);
        (await Gate(kit, "a")).Should().StartWith("OPEN:");
        (await kit.Db.KeyExistsAsync(kit.Names.ClearLock(kit.Group, "a"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_transition_during_the_sweep_is_followed_by_a_close_under_the_new_identity()
    {
        await using var kit = await StartedAsync();
        var sweeps = 0;
        var hooks = new ClearHooks(
            _ => Task.CompletedTask,
            async _ =>
            {
                if (++sweeps == 1)
                {
                    var current = (await kit.Store.ReadAsync(kit.Group, Ct))!;
                    (await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(current), StateKit.TargetFrom(current, "S2", "fwd", 0), null, Ct)).Outcome.Should().Be(TransitionOutcome.Completed);
                }
            },
            _ => Task.CompletedTask);

        (await Coordinator(kit).ClearAsync(kit.Group, "a", hooks, Ct)).Should().Be(ClearOutcome.Cleared);

        sweeps.Should().Be(2);
        (await Gate(kit, "a")).Should().StartWith("OPEN:");
    }

    [Fact]
    public async Task A_sweep_that_throws_reopens_the_gate_and_releases_the_lock()
    {
        await using var kit = await StartedAsync();
        var hooks = new ClearHooks(_ => Task.CompletedTask, _ => throw new InvalidOperationException("sweep failed"), _ => Task.CompletedTask);

        var act = () => Coordinator(kit).ClearAsync(kit.Group, "a", hooks, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await Gate(kit, "a")).Should().StartWith("OPEN:");
        (await kit.Db.KeyExistsAsync(kit.Names.ClearLock(kit.Group, "a"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_lock_lost_during_the_sweep_stops_it_and_reopens_only_what_this_clear_closed()
    {
        await using var kit = await StartedAsync();
        var lockKey = kit.Names.ClearLock(kit.Group, "a");
        var created = kit.Time.TimersCreated;
        var hooks = new ClearHooks(
            _ => Task.CompletedTask,
            async token =>
            {
                await kit.Db.StringSetAsync(lockKey, "thief");
                await RenewalNoticesAsync(kit, created, token);
            },
            _ => Task.CompletedTask);

        (await Coordinator(kit).ClearAsync(kit.Group, "a", hooks, Ct)).Should().Be(ClearOutcome.Failed);

        (await Gate(kit, "a")).Should().StartWith("OPEN:");
        (await kit.Db.StringGetAsync(lockKey)).Should().Be("thief", "a lock that is not ours is not released");
        await kit.Db.KeyDeleteAsync(lockKey);
    }

    [Fact]
    public async Task A_lock_that_expired_is_not_taken_again_by_the_renewal_and_the_gate_the_leader_reopened_stays_open()
    {
        await using var kit = await StartedAsync();
        var control = new MoveScripts(kit.ControlDb, true, fixture.ServerVersion);
        var lockKey = kit.Names.ClearLock(kit.Group, "a");
        var reopener = new StuckGateReopener(kit.Records, control, kit.Db, kit.Names, kit.Options, kit.Time);
        var created = kit.Time.TimersCreated;
        var hooks = new ClearHooks(
            _ => Task.CompletedTask,
            async token =>
            {
                // The Clear was suspended past its lock's life: the lock is gone and the leader reopens the gate it finds closed.
                await kit.Db.KeyDeleteAsync(lockKey);
                (await reopener.CheckAsync(kit.Group, (await kit.Store.ReadAsync(kit.Group, Ct))!, Ct)).Reopened.Should().Equal("a");
                await RenewalNoticesAsync(kit, created, token);
            },
            _ => Task.CompletedTask);

        (await Coordinator(kit).ClearAsync(kit.Group, "a", hooks, Ct)).Should().Be(ClearOutcome.Failed);

        (await kit.Db.KeyExistsAsync(lockKey)).Should().BeFalse("renewal never creates the lock again");
        (await Gate(kit, "a")).Should().StartWith("OPEN:");
    }

    [Fact]
    public async Task A_clear_suspended_past_the_life_of_its_lock_fails_on_the_clock_even_when_no_renewal_failed()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true, new KeyMoveStateOptions { ExpectedReplicasPerMaster = 0, ClearLockRenewInterval = TimeSpan.FromHours(1) });
        var init = await kit.InitAsync("a", "b");
        (await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, Ct)).Outcome.Should().Be(TransitionOutcome.Completed);
        var hooks = new ClearHooks(_ => Task.CompletedTask, _ => Task.Run(() => kit.Time.Advance(kit.Options.ClearLockTtl + TimeSpan.FromSeconds(1))), _ => Task.CompletedTask);

        (await Coordinator(kit).ClearAsync(kit.Group, "a", hooks, Ct)).Should().Be(ClearOutcome.Failed);

        (await Gate(kit, "a")).Should().StartWith("OPEN:");
    }

    [Fact]
    public async Task The_leader_reopens_a_gate_left_closed_once_its_lock_has_expired_and_not_before()
    {
        await using var kit = await StartedAsync();
        var control = new MoveScripts(kit.ControlDb, true, fixture.ServerVersion);
        var state = (await kit.Store.ReadAsync(kit.Group, Ct))!;
        var record = kit.Names.Control(kit.Group, 0);
        (await control.GateAsync(record, state.Id, "a", "CLOSED:crashed")).Should().Be(MoveScripts.Written);
        var lockKey = kit.Names.ClearLock(kit.Group, "a");
        await kit.Db.StringSetAsync(lockKey, "dead-owner", TimeSpan.FromMilliseconds(300));
        var reopener = new StuckGateReopener(kit.Records, control, kit.Db, kit.Names, kit.Options, kit.Time);

        var held = await reopener.CheckAsync(kit.Group, state, Ct);
        held.Reopened.Should().BeEmpty("the owner's lock is still live");
        (await Gate(kit, "a")).Should().Be("CLOSED:crashed");

        await TimeSteps.UntilAsync(() => !kit.Db.KeyExists(lockKey));
        var expired = await reopener.CheckAsync(kit.Group, state, Ct);

        expired.Reopened.Should().Equal("a");
        (await Gate(kit, "a")).Should().StartWith("OPEN:");
        (await Gate(kit, "b")).Should().StartWith("OPEN:");
    }

    [Fact]
    public async Task A_reopen_expecting_the_value_it_read_never_undoes_a_newer_close()
    {
        await using var kit = await StartedAsync();
        var control = new MoveScripts(kit.ControlDb, true, fixture.ServerVersion);
        var state = (await kit.Store.ReadAsync(kit.Group, Ct))!;
        var record = kit.Names.Control(kit.Group, 0);

        await control.GateAsync(record, state.Id, "a", "CLOSED:newer");
        (await control.GateAsync(record, state.Id, "a", "OPEN:late", "CLOSED:older")).Should().Be(MoveScripts.ExpectationFailed);

        (await Gate(kit, "a")).Should().Be("CLOSED:newer");
    }

    [Fact]
    public async Task A_gate_closed_past_the_stuck_age_is_reported_even_while_its_lock_is_live()
    {
        await using var kit = await StartedAsync();
        var control = new MoveScripts(kit.ControlDb, true, fixture.ServerVersion);
        var state = (await kit.Store.ReadAsync(kit.Group, Ct))!;
        await control.GateAsync(kit.Names.Control(kit.Group, 0), state.Id, "a", "CLOSED:slow");
        var lockKey = kit.Names.ClearLock(kit.Group, "a");
        await kit.Db.StringSetAsync(lockKey, "owner", TimeSpan.FromMinutes(5));
        var reopener = new StuckGateReopener(kit.Records, control, kit.Db, kit.Names, kit.Options, kit.Time);

        (await reopener.CheckAsync(kit.Group, state, Ct)).Stuck.Should().BeEmpty();
        kit.Time.Advance(kit.Options.GateStuckAfter + TimeSpan.FromSeconds(1));
        var late = await reopener.CheckAsync(kit.Group, state, Ct);

        late.Stuck.Should().Equal("a");
        late.Reopened.Should().BeEmpty();
        await kit.Db.KeyDeleteAsync(lockKey);
    }

    // Moves the clock once, and only when the renewal loop's own delay is the one live timer: a timer of anything else must not release it early.
    private static async Task RenewalNoticesAsync(StateKit kit, int timersBefore, CancellationToken sweepToken)
    {
        await TimeSteps.DelayArmedAfterAsync(kit.Time, timersBefore);
        kit.Time.Advance(kit.Options.ClearLockRenewInterval);
        await TimeSteps.UntilAsync(() => sweepToken.IsCancellationRequested);
        sweepToken.ThrowIfCancellationRequested();
    }

    private static ClearHooks Hooks(Action onSweep) => new(_ => Task.CompletedTask, _ => Task.Run(onSweep), _ => Task.CompletedTask);

    private static async Task<string?> Gate(StateKit kit, string cid) => await kit.ControlDb.HashGetAsync(kit.Names.Control(kit.Group, 0), "c:" + cid);

    private ClearCoordinator Coordinator(StateKit kit) =>
        new(kit.Store, kit.Scripts, new MoveScripts(kit.ControlDb, true, fixture.ServerVersion), kit.Records, kit.Names, kit.Options, kit.Time);

    private async Task<StateKit> StartedAsync()
    {
        var kit = await StateKit.StandaloneAsync(fixture, true);
        var init = await kit.InitAsync("a", "b");
        (await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, Ct)).Outcome.Should().Be(TransitionOutcome.Completed);
        return kit;
    }
}
