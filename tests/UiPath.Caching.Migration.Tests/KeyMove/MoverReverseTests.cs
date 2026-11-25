using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The reverse mover: a scan of N that moves every owned key back to L under GL.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoverReverseTests(RedisFixture fixture)
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reverse_pass_moves_every_key_of_N_to_L_short_ones_included_and_records_its_pass()
    {
        await using var kit = await MoverKit.CreateAsync(fixture, groupMin: Hour);
        await kit.SeedNativeAsync(new Dictionary<string, (string, TimeSpan?)>
        {
            ["ok"] = ("v", null),
            ["ttl"] = ("v2", Hour),
            ["short"] = ("v3", TimeSpan.FromMinutes(1)),
        });

        await kit.RunOnePassAsync(kit.Host(state: () => kit.State(direction: "rev")), "rev");

        foreach (var (key, value) in new[] { ("ok", "v"), ("ttl", "v2"), ("short", "v3") })
        {
            (await kit.Kit.GetAsync(kit.Kit.L(key))).Should().Be(value);
            (await kit.Kit.ExistsAsync(kit.Kit.N(key))).Should().BeFalse();
        }

        (await kit.Db.KeyTimeToLiveAsync(kit.Kit.L("ttl")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(59));
        (await kit.Kit.ExistsAsync(kit.Kit.Names.ReverseFence(kit.Kit.L("ok")).ToString())).Should().BeTrue();
        await kit.WaitProgressAsync(new Dictionary<string, long> { ["moved"] = 3 });
        (await kit.PassedAsync("fwd")).Should().BeFalse("a reverse pass records under its own direction");
    }

    [Fact]
    public async Task A_reverse_pass_replaces_an_older_copy_in_L_because_N_is_the_authority()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedNativeAsync(new Dictionary<string, (string, TimeSpan?)> { ["k"] = ("new", null) });
        await kit.Db.StringSetAsync(kit.Kit.L("k"), "old");

        await kit.RunOnePassAsync(kit.Host(state: () => kit.State(direction: "rev")), "rev");

        (await kit.Kit.GetAsync(kit.Kit.L("k"))).Should().Be("new");
        (await kit.Kit.ExistsAsync(kit.Kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_forward_pass_leaves_native_only_keys_in_N()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("fwd");
        await kit.Db.StringSetAsync(kit.Kit.N("back"), "n");

        await kit.RunOnePassAsync(kit.Host());

        (await kit.Kit.GetAsync(kit.Kit.N("back"))).Should().Be("n", "a forward pass scans L only");
        (await kit.Kit.ExistsAsync(kit.Kit.L("back"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_reverse_pass_preserves_legacy_only_keys_while_moving_native_keys()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedNativeAsync(new Dictionary<string, (string, TimeSpan?)> { ["native-only"] = ("native", null) });
        await kit.Kit.PrepareAsync("legacy-only");
        await kit.Db.StringSetAsync(kit.Kit.L("legacy-only"), "legacy");

        await kit.RunOnePassAsync(kit.Host(state: () => kit.State(direction: "rev")), "rev");

        (await kit.Kit.GetAsync(kit.Kit.L("legacy-only"))).Should().Be("legacy");
        (await kit.Kit.GetAsync(kit.Kit.L("native-only"))).Should().Be("native");
        (await kit.Kit.ExistsAsync(kit.Kit.N("native-only"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_reverse_pass_does_not_start_while_the_node_acts_under_the_other_direction()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedNativeAsync(new Dictionary<string, (string, TimeSpan?)> { ["k"] = ("v", null) });
        var stateRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = kit.Host(state: () =>
        {
            stateRead.TrySetResult();
            return kit.State();
        });

        host.Start();
        try
        {
            await stateRead.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await Task.Delay(TimeSpan.FromMilliseconds(300), TimeProvider.System, Ct);

            host.RunningUnits.Should().BeEmpty();
            (await kit.PassedAsync("fwd")).Should().BeFalse();
            (await kit.PassedAsync("rev")).Should().BeFalse();
        }
        finally
        {
            await host.StopAsync();
        }

        (await kit.Kit.GetAsync(kit.Kit.N("k"))).Should().Be("v");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_oversized_native_value_clears_its_legacy_copy_and_reads_as_a_miss_after_the_release_sweep(bool useScripts)
    {
        await using var kit = await MoverKit.CreateAsync(fixture, useScripts: useScripts);
        await kit.SeedNativeAsync(new Dictionary<string, (string, TimeSpan?)> { ["k"] = ("big:current", null) });
        var legacy = kit.Kit.L("k");
        var fence = kit.Kit.Names.ReverseFence(legacy);
        await kit.Db.StringSetAsync(legacy, "old");
        await kit.Db.StringSetAsync(fence, "before");

        await kit.RunOnePassAsync(kit.Host(state: () => kit.State(direction: "rev")), "rev");

        await kit.WaitProgressAsync(new Dictionary<string, long> { ["skipped:too_large"] = 1 });
        (await kit.Kit.GetAsync(kit.Kit.N("k"))).Should().Be("big:current");
        (await kit.Kit.ExistsAsync(legacy)).Should().BeFalse();
        ((string?)await kit.Db.StringGetAsync(fence)).Should().NotBeNullOrEmpty().And.NotBe("before");

        await SweepReleasedNativeAsync(kit, useScripts);

        (await kit.Kit.ExistsAsync(kit.Kit.N("k"))).Should().BeFalse();
        (await kit.Kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Absent);
    }

    [Fact]
    public async Task An_oversized_native_snapshot_does_not_delete_a_newer_fenced_reverse_write()
    {
        var schedule = new PausingSchedule(SchedulePointStep.Publish);
        await using var kit = await MoverKit.CreateAsync(fixture, schedule: schedule);
        await kit.SeedNativeAsync(new Dictionary<string, (string, TimeSpan?)> { ["k"] = ("big:current", null) });
        await kit.Db.StringSetAsync(kit.Kit.L("k"), "old");

        var moving = kit.Target.MoveBackAsync("k", kit.Kit.Id, AlwaysOwned.Instance, Ct).AsTask();
        try
        {
            await schedule.ReachedAsync.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            (await kit.Kit.Client.SetAsync("k", new Item("new"), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        }
        finally
        {
            schedule.Release();
        }

        (await moving).SkipReason.Should().Be("duplicate");
        (await kit.Kit.GetAsync(kit.Kit.L("k"))).Should().Be("new");
    }

    [Theory]
    [InlineData("S3")]
    [InlineData("S4")]
    public async Task A_reverse_move_does_not_write_for_a_cache_without_an_origin(string state)
    {
        await using var kit = await MoverKit.CreateAsync(fixture, noOrigin: true);
        await kit.SeedNativeAsync(new Dictionary<string, (string, TimeSpan?)> { ["k"] = ("native", null) });
        kit.Kit.ApplyRules(state, direction: "rev");
        await kit.Db.StringSetAsync(kit.Kit.L("k"), "legacy");

        var result = await kit.Target.MoveBackAsync("k", kit.Kit.Id, AlwaysOwned.Instance, Ct);

        result.Moved.Should().BeFalse();
        (await kit.Kit.GetAsync(kit.Kit.L("k"))).Should().Be("legacy");
        (await kit.Kit.GetAsync(kit.Kit.N("k"))).Should().Be("native");
    }

    [Fact]
    public async Task A_target_with_no_reverse_move_leaves_the_key_in_N_and_its_pass_is_no_pass()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedNativeAsync(new Dictionary<string, (string, TimeSpan?)> { ["k"] = ("v", null) });
        var host = kit.Host(state: () => kit.State(direction: "rev"), wrap: inner => new CountingTarget(inner));

        host.Start();
        try
        {
            await MoverKit.WaitAsync(async () => (await kit.ProgressAsync()).ContainsKey("skipped:no_reverse"));
        }
        finally
        {
            await host.StopAsync();
        }

        (await kit.Kit.GetAsync(kit.Kit.N("k"))).Should().Be("v");
        (await kit.PassedAsync("rev")).Should().BeFalse("a key that could not be moved back fails the pass");
    }

    [Fact]
    public async Task A_finished_reverse_pass_is_the_evidence_of_S3_rev_and_not_of_the_forward_move()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedNativeAsync(new Dictionary<string, (string, TimeSpan?)> { ["k"] = ("v", null) });
        var reverse = kit.State(direction: "rev");
        var passes = new MoverPasses(kit.Units, kit.Db, kit.Kit.Names, (_, _) => Task.FromResult<StateRecord?>(reverse));

        (await passes.FinishedAfterAckAsync(kit.Kit.Group, "rev", Ct)).Should().BeFalse("no pass has run");
        await kit.RunOnePassAsync(kit.Host(state: () => reverse), "rev");

        (await passes.FinishedAfterAckAsync(kit.Kit.Group, "rev", Ct)).Should().BeTrue();
        (await passes.FinishedAfterAckAsync(kit.Kit.Group, "fwd", Ct)).Should().BeFalse();
        (await passes.FinishedAfterAckAsync(kit.Kit.Group, "other", Ct)).Should().BeFalse();
    }

    private static async Task SweepReleasedNativeAsync(MoverKit mover, bool useScripts)
    {
        var kit = mover.Kit;
        await kit.SetStateAsync("S4", direction: "rev");
        await kit.Db.HashSetAsync(kit.Names.State(kit.Group),
        [
            new(StateFields.State, "S4"),
            new(StateFields.Direction, "rev"),
            new(StateFields.Ep, kit.Ep),
            new(StateFields.Id, kit.Id),
            new(StateFields.Durable, "1"),
            new(StateFields.Caches, kit.Cid),
        ]);

        var topology = new MultiplexerTopology(kit.Mux, string.Empty, useScripts, kit.Mux.GetServers().Single().Version, 0);
        var store = new StateStore(kit.Db, kit.Names, kit.Scripts, new ControlSampler(kit.Db, kit.Names, topology), kit.Clock);
        var scanner = new MasterScanner(() => kit.Mux.GetServers(), topology, kit.Db.Database, string.Empty, kit.Clock, TimeSpan.Zero);
        var data = new GroupDataOps(scanner, mover.Space, store, kit.Scripts, kit.Scripts, kit.Names);
        await data.PurgeNativeAsync(kit.Group, new DataGuard(kit.Id, null), Ct);
    }
}
