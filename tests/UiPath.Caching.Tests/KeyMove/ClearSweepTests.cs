using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The coordinator driving the real sweep of N, with the lock lost during it and with a gate that cannot be closed on one slot of a cluster.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class ClearSweepTests(RedisContainerFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_lock_lost_in_the_middle_of_the_sweep_stops_it_and_reopens_only_the_gate_this_clear_closed()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var init = await kit.InitAsync("a", "b");
        (await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, Ct)).Outcome.Should().Be(TransitionOutcome.Completed);
        var tag = "clr" + Guid.NewGuid().ToString("N")[..10];
        var space = new FakeKeySpace(tag + ":n:", tag + ":l:");
        var keys = Enumerable.Range(0, 30).Select(n => $"{tag}:n:a:k{n}").ToArray();
        await kit.Db.StringSetAsync([.. keys.Select(k => new KeyValuePair<RedisKey, RedisValue>(k, "v"))]);
        var control = new MoveScripts(kit.ControlDb, true, fixture.ServerVersion);
        var lockKey = kit.Names.ClearLock(kit.Group, "a");
        var state = (await kit.Store.ReadAsync(kit.Group, Ct))!;
        var asked = 0;
        CancellationToken sweepToken = default;
        space.OnOwnerOf = _ =>
        {
            if (++asked == 3)
            {
                // Another Clear took the lock and closed the gate; this one's renewal notices at its next tick.
                kit.Db.StringSet(lockKey, "thief");
                control.GateAsync(kit.Names.Control(kit.Group, 0), state.Id, "a", "CLOSED:thief").GetAwaiter().GetResult();
                kit.Time.Advance(kit.Options.ClearLockRenewInterval);
                SpinWait.SpinUntil(() => sweepToken.IsCancellationRequested, TimeSpan.FromSeconds(10)).Should().BeTrue("the renewal failed and cancelled the sweep");
            }
        };
        var scanner = new MasterScanner(() => kit.Mux.GetServers(), kit.Topology, 0, string.Empty, TimeProvider.System, TimeSpan.Zero);
        var sweep = new KeyMoveSweep(scanner, space, kit.ControlDb, kit.Names, 1);
        var created = kit.Time.TimersCreated;
        var hooks = new ClearHooks(
            _ => Task.CompletedTask,
            async token =>
            {
                sweepToken = token;
                await TimeSteps.DelayArmedAfterAsync(kit.Time, created);
                await sweep.SweepAsync(kit.Group, "a", token);
            },
            _ => Task.CompletedTask);
        var coordinator = new ClearCoordinator(kit.Store, kit.Scripts, control, kit.Records, kit.Names, kit.Options, kit.Time);

        try
        {
            (await coordinator.ClearAsync(kit.Group, "a", hooks, Ct)).Should().Be(ClearOutcome.Failed);

            var left = 0;
            foreach (var key in keys)
            {
                left += await kit.Db.KeyExistsAsync(key) ? 1 : 0;
            }

            left.Should().BeGreaterThan(20, "the sweep stopped when the lock was lost instead of finishing the cache");
            asked.Should().BeLessThan(10);
            (await kit.ControlDb.HashGetAsync(kit.Names.Control(kit.Group, 0), "c:a")).ToString().Should().Be("CLOSED:thief", "a gate that is not this Clear's is not reopened");
            (await kit.Db.StringGetAsync(lockKey)).Should().Be("thief");
        }
        finally
        {
            await kit.Db.KeyDeleteAsync([lockKey, .. keys.Select(k => (RedisKey)k)]);
        }
    }

    [Fact]
    public async Task A_lease_that_lapses_while_the_renewal_is_pending_stops_the_sweep_before_the_next_batch_with_no_cancellation()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true, new KeyMoveStateOptions { ExpectedReplicasPerMaster = 0, ClearLockRenewInterval = TimeSpan.FromHours(1) });
        var init = await kit.InitAsync("a", "b");
        (await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, Ct)).Outcome.Should().Be(TransitionOutcome.Completed);
        var tag = "clr" + Guid.NewGuid().ToString("N")[..10];
        var space = new FakeKeySpace(tag + ":n:", tag + ":l:");
        var keys = Enumerable.Range(0, 30).Select(n => $"{tag}:n:a:k{n}").ToArray();
        await kit.Db.StringSetAsync([.. keys.Select(k => new KeyValuePair<RedisKey, RedisValue>(k, "v"))]);
        var control = new MoveScripts(kit.ControlDb, true, fixture.ServerVersion);
        var state = (await kit.Store.ReadAsync(kit.Group, Ct))!;
        var asked = 0;
        space.OnOwnerOf = _ =>
        {
            if (++asked == 3)
            {
                // Suspended past the life of the lock: the renewal has not run, so nothing is cancelled, and the leader reopens the gate.
                kit.Time.Advance(kit.Options.ClearLockTtl + TimeSpan.FromSeconds(1));
                control.GateAsync(kit.Names.Control(kit.Group, 0), state.Id, "a", "CLOSED:thief").GetAwaiter().GetResult();
            }
        };
        var scanner = new MasterScanner(() => kit.Mux.GetServers(), kit.Topology, 0, string.Empty, TimeProvider.System, TimeSpan.Zero);
        var sweep = new KeyMoveSweep(scanner, space, kit.ControlDb, kit.Names, 1);
        var cancelledWhenStopped = true;
        var hooks = new ClearHooks(_ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask)
        {
            GuardedSweep = async (lockLost, token) =>
            {
                try
                {
                    await sweep.SweepAsync(kit.Group, "a", lockLost, token);
                }
                catch (OperationCanceledException)
                {
                    cancelledWhenStopped = token.IsCancellationRequested;
                    throw;
                }
            },
        };
        var coordinator = new ClearCoordinator(kit.Store, kit.Scripts, control, kit.Records, kit.Names, kit.Options, kit.Time);

        try
        {
            (await coordinator.ClearAsync(kit.Group, "a", hooks, Ct)).Should().Be(ClearOutcome.Failed);

            cancelledWhenStopped.Should().BeFalse("the sweep stopped on the validity of the lock, not on a cancellation");
            var left = 0;
            foreach (var key in keys)
            {
                left += await kit.Db.KeyExistsAsync(key) ? 1 : 0;
            }

            left.Should().BeGreaterThan(20, "the batches after the lapse were never unlinked");
            (await kit.ControlDb.HashGetAsync(kit.Names.Control(kit.Group, 0), "c:a")).ToString().Should().Be("CLOSED:thief", "a gate that is not this Clear's is not reopened");
        }
        finally
        {
            await kit.Db.KeyDeleteAsync([.. keys.Select(k => (RedisKey)k)]);
        }
    }

    [Fact]
    public async Task A_gate_that_cannot_be_closed_on_one_slot_reopens_every_gate_this_clear_closed_and_nothing_else_and_sweeps_nothing()
    {
        await using var kit = await StateKit.ClusterAsync(true);
        var init = await kit.InitAsync("a", "b");
        (await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, Ct)).Outcome.Should().Be(TransitionOutcome.Completed);
        var control = new MoveScripts(kit.ControlDb, true, kit.Mux.GetServers().Min(s => s.Version)!);
        var state = (await kit.Store.ReadAsync(kit.Group, Ct))!;
        var slots = kit.Names.SlotSet();
        const int broken = 15000;
        foreach (var chunk in slots.Chunk(512))
        {
            // Another cache's gates are closed by someone else, on every slot.
            var closed = await Task.WhenAll(chunk.Select(s => control.GateAsync(kit.Names.Control(kit.Group, s), state.Id, "b", "CLOSED:other")));
            closed.Should().OnlyContain(c => c == MoveScripts.Written);
        }

        await kit.ControlDb.KeyDeleteAsync(kit.Names.Control(kit.Group, broken));
        var swept = false;
        var hooks = new ClearHooks(_ => Task.CompletedTask, _ => Task.Run(() => swept = true), _ => Task.CompletedTask);
        var coordinator = new ClearCoordinator(kit.Store, kit.Scripts, control, kit.Records, kit.Names, kit.Options, kit.Time);

        (await coordinator.ClearAsync(kit.Group, "a", hooks, Ct)).Should().Be(ClearOutcome.Unavailable);

        swept.Should().BeFalse();
        foreach (var chunk in slots.Where(s => s != broken).Chunk(512))
        {
            var gates = await Task.WhenAll(chunk.Select(async s => (a: (string?)await kit.ControlDb.HashGetAsync(kit.Names.Control(kit.Group, s), "c:a"), b: (string?)await kit.ControlDb.HashGetAsync(kit.Names.Control(kit.Group, s), "c:b"))));
            gates.Should().OnlyContain(g => g.a!.StartsWith("OPEN:", StringComparison.Ordinal), "the rollback reopened what this Clear closed");
            gates.Should().OnlyContain(g => g.b == "CLOSED:other", "another gate of the group is not this Clear's to reopen");
        }

        (await kit.Db.KeyExistsAsync(kit.Names.ClearLock(kit.Group, "a"))).Should().BeFalse();
    }
}
