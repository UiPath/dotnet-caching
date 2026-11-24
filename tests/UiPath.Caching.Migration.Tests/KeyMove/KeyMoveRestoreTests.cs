using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <summary>Exercises interrupted transitions and rollback with live data in Redis.</summary>
[Collection("RedisIntegration")]
public sealed class KeyMoveRestoreTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("S0", "S1", 3)]
    [InlineData("S1", "S2", 3)]
    [InlineData("S2", "S3", 3)]
    public async Task An_interrupted_transition_restores_acknowledged_values_on_L(string committed, string interrupted, int requestedStage) =>
        await RestoreLedgerAsync(committed, interrupted, requestedStage, native: false);

    [Theory]
    [InlineData("S3", "S4", 4)]
    [InlineData("S4", "S5", 5)]
    public async Task An_interrupted_transition_restores_acknowledged_values_on_N(string committed, string interrupted, int requestedStage) =>
        await RestoreLedgerAsync(committed, interrupted, requestedStage, native: true);

    [Theory]
    [InlineData("S3")]
    [InlineData("S4")]
    public async Task Native_reads_continue_while_reverse_movement_is_pending(string committed)
    {
        await using var host = await MovingCacheHost.CreateAsync(fixture);
        await DriveToAsync(host, committed);
        await EnterReverseAsync(host);
        await host.AssertValueAsync("kept", [1]);

        await host.ExpectAlertAsync("stage_pending");
        (await host.NativeKeysAsync()).Should().Equal(host.Native("kept"));
        (await host.LegacyKeysAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Writes_keep_working_after_the_reverse_barrier()
    {
        Assert.Skip("not built yet: the reverse write forms");
        await using var host = await MovingCacheHost.CreateAsync(fixture);
        await DriveToAsync(host, "S3");
        await EnterReverseAsync(host);
        await host.WriteAsync("kept", [2]);
        await host.AssertValueAsync("kept", [2]);
        (await host.LegacyKeysAsync()).Should().Equal(host.Legacy("kept"));
    }

    [Fact]
    public async Task The_reverse_mover_returns_untouched_native_values_to_the_legacy_path()
    {
        Assert.Skip("not built yet: the reverse mover");
        await using var host = await MovingCacheHost.CreateAsync(fixture);
        await DriveToAsync(host, "S4");
        await EnterReverseAsync(host);
        await host.AdvanceAsync("S4", "rev");
        await host.ApproveAsync(ApprovalKinds.Release);
        await host.AdvanceAsync("S0", "none");
        await host.AssertValueAsync("kept", [1]);
        (await host.LegacyKeysAsync()).Should().Equal(host.Legacy("kept"));
        (await host.NativeKeysAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Closed_data_stays_native_after_control_repair_and_a_lowered_stage_request()
    {
        await using var host = await MovingCacheHost.CreateAsync(fixture);
        var ledger = await SeedLedgerAsync(host);
        await DriveToAsync(host, "S5");
        await ApplyWritesAsync(host, ledger, revision: 1, acceptsWrites: true, Ct);
        var closed = await host.StateAsync();
        await host.Db.KeyDeleteAsync(host.Names.ControlFor(MovingCacheHost.Group, host.Native("kept"))).WaitAsync(Ct);

        await TestPoll.WaitAsync(
            async () => await host.StateAsync() is { State: "S5", Durable: true } repaired && repaired.Ep > closed.Ep,
            () => "The leader did not repair the closed control record.");
        await host.SetStageAsync(2);
        await host.ExpectAlertAsync("flag_below_closed");
        await host.ApproveAsync(ApprovalKinds.Abandon);
        await ApplyWritesAsync(host, ledger, revision: 2, acceptsWrites: true, Ct);
        await host.ExpectAlertAsync("abandon_refused");
        await host.AdvanceAsync("S5");
        await AssertLedgerAsync(host, ledger, native: true);
        await host.SeedAsync("legacy-only", [9]);
        (await host.Cache.GetAsync("legacy-only", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Close_refuses_legacy_residue_that_grew_after_drain()
    {
        await using var host = await MovingCacheHost.CreateAsync(fixture);
        await DriveToAsync(host, "S4");
        await host.ApproveAsync(ApprovalKinds.Close);
        await host.SeedAsync("late-legacy-value", [9]);
        await host.SetStageAsync(5);

        await host.ExpectAlertAsync("close_pending", KeyMoveGateIds.CloseInventory);
        (await host.LegacyKeysAsync()).Should().Equal(host.Legacy("late-legacy-value"));
        await host.AssertValueAsync("kept", [1]);
        await host.WriteAsync("late-legacy-value", [10]);
        await host.AdvanceAsync("S5");
        await host.AssertValueAsync("late-legacy-value", [10]);
        (await host.LegacyKeysAsync()).Should().BeEmpty();
    }

    private static async Task<Dictionary<string, List<long>?>> SeedLedgerAsync(MovingCacheHost host)
    {
        var ledger = new Dictionary<string, List<long>?> { ["kept"] = [1] };
        for (var i = 0; i < 4; i++)
        {
            var key = $"writer-{i}";
            List<long> value = [i, 0];
            await host.SeedAsync(key, value);
            ledger.Add(key, value);
        }

        return ledger;
    }

    private static async Task ApplyWritesAsync(
        MovingCacheHost host,
        Dictionary<string, List<long>?> ledger,
        long revision,
        bool acceptsWrites,
        CancellationToken token)
    {
        var writes = ledger.Select(async entry =>
        {
            List<long> value = [entry.Value![0], revision];
            var acknowledged = await host.Cache.SetAsync(entry.Key, value, token);
            return (entry.Key, Value: value, Acknowledged: acknowledged);
        });
        foreach (var (key, value, acknowledged) in await Task.WhenAll(writes))
        {
            acknowledged.Should().Be(acceptsWrites, $"the write for {key} at revision {revision} must have the expected outcome");
            if (acknowledged)
            {
                ledger[key] = value;
            }
        }
    }

    private static async Task AssertLedgerAsync(MovingCacheHost host, IReadOnlyDictionary<string, List<long>?> ledger, bool native)
    {
        if (native)
        {
            await host.AssertNativeLedgerAsync(ledger);
            (await host.LegacyKeysAsync()).Should().BeEmpty();
        }
        else
        {
            foreach (var (key, value) in ledger)
            {
                var bytes = (byte[]?)(await host.Db.StringGetAsync(host.Legacy(key), CommandFlags.DemandMaster).WaitAsync(Ct));
                LegacyEnvelope.Read<List<long>>(bytes).Should().Equal(value, $"Redis L must contain the last acknowledged value for {key}");
            }

            (await host.LegacyKeysAsync()).Should().BeEquivalentTo(ledger.Keys.Select(host.Legacy));
            (await host.NativeKeysAsync()).Should().BeEmpty();
        }

        foreach (var (key, value) in ledger)
        {
            await host.AssertValueAsync(key, value);
        }
    }

    private static async Task EnterReverseAsync(MovingCacheHost host)
    {
        await host.SetStageAsync(2);
        await host.AdvanceAsync("S1", "rev");
        await host.AdvanceAsync("S3", "rev");
    }

    private static async Task DriveToAsync(MovingCacheHost host, string stage)
    {
        await host.SeedAsync("kept", [1]);
        if (stage == "S0")
        {
            return;
        }

        await host.PrepareMoveAsync();
        await host.SetStageAsync(3);
        await host.AdvanceAsync("S1");
        if (stage == "S1")
        {
            return;
        }

        await host.AdvanceAsync("S2");
        if (stage == "S2")
        {
            return;
        }

        await host.AdvanceAsync("S3");
        await host.StartMoverAsync();
        await TestPoll.WaitAsync(
            async () => await host.Db.KeyExistsAsync(host.Native("kept")).WaitAsync(Ct) && !await host.Db.KeyExistsAsync(host.Legacy("kept")).WaitAsync(Ct),
            () => "The mover did not transfer the seed.");
        if (stage == "S3")
        {
            await host.WaitForMoverPassAsync();
            return;
        }

        await host.SetStageAsync(4);
        await host.AdvanceAsync("S4");
        if (stage == "S4")
        {
            return;
        }

        await host.ApproveAsync(ApprovalKinds.Close);
        await host.SetStageAsync(5);
        await host.AdvanceAsync("S5");
    }

    private async Task RestoreLedgerAsync(string committed, string interrupted, int requestedStage, bool native)
    {
        await using var host = await MovingCacheHost.CreateAsync(fixture);
        var ledger = await SeedLedgerAsync(host);
        await DriveToAsync(host, committed);
        if (native)
        {
            await host.StopMoverAsync();
        }

        if (committed == "S0")
        {
            await host.PrepareMoveAsync();
        }

        var before = await host.StateAsync();
        var acceptsWrites = committed != "S2";
        await ApplyWritesAsync(host, ledger, revision: 1, acceptsWrites, Ct);
        if (interrupted == "S5")
        {
            await host.ApproveAsync(ApprovalKinds.Close);
        }

        (await host.InterruptTransitionAsync(interrupted, requestedStage)).Should().Be(TransitionOutcome.BarrierStuck);
        var pending = await host.StateAsync();
        pending.State.Should().Be(interrupted);
        pending.Durable.Should().BeFalse();

        await host.WaitForPendingRulesAsync(pending.Id);
        foreach (var (key, value) in ledger)
        {
            await host.AssertValueAsync(key, value);
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var writes = ApplyWritesAsync(host, ledger, revision: 2, acceptsWrites, stop.Token);
        try
        {
            await host.SetStageAsync(requestedStage - 1);
            var step = await host.StepAsync();
            step!.Plan.Kind.Should().Be(StepKind.Abandon);
            step.Result!.Outcome.Should().Be(TransitionOutcome.Completed);
            var restored = await host.AdvanceAsync(committed, before.Direction);
            restored.Ep.Should().BeGreaterThan(pending.Ep);
            restored.Id.Should().NotBe(before.Id);
            restored.NPop.Should().Be(before.NPop);
            await writes.WaitAsync(TimeSpan.FromSeconds(10), MovingCacheHost.Clock, Ct);

            await AssertLedgerAsync(host, ledger, native);
            if (committed is "S1" or "S2")
            {
                await host.AdvanceAsync("S0", "none");
                await AssertLedgerAsync(host, ledger, native);
            }

            await ApplyWritesAsync(host, ledger, revision: 3, acceptsWrites: true, Ct);
            await AssertLedgerAsync(host, ledger, native);
        }
        finally
        {
            await Task.WhenAll(stop.CancelAsync(), writes).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
