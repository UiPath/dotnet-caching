using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The retire gate, the retire sweep, the consent that skips the gate, and the fence that keeps an old marker from reaching L.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class RetireGateTests(RedisContainerFixture fixture)
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task The_retire_step_waits_while_a_marker_of_the_retiring_generation_exists_alerts_reverse_blocked_after_RetireWait_and_stays_in_S1_reverse()
    {
        await using var kit = await DriverKit.CreateAsync(fixture, retireSweeper: false);
        var (generation, ls) = await ToReverseWithMarkersAsync(kit, 3);

        for (var n = 0; n < 4; n++)
        {
            await kit.StepAsync(Ten);
        }

        (await kit.StateAsync()).State.Should().Be("S1");
        kit.Alerts.Should().Contain(("stage_pending", "retire:3"));
        kit.Alerts.Should().NotContain(a => a.Code == "reverse_blocked");
        for (var n = 0; n < 4; n++)
        {
            await kit.StepAsync(Ten);
        }

        (await kit.StateAsync()).Should().Match<StateRecord>(s => s.State == "S1" && s.Direction == "rev");
        kit.Alerts.Should().Contain(("reverse_blocked", "retire:3"));
        generation.Should().NotBeEmpty();
        foreach (var l in ls)
        {
            (await kit.Kit.ControlDb.KeyExistsAsync(l)).Should().BeTrue();
        }

    }

    [Fact]
    public async Task A_retire_sweep_that_keeps_failing_neither_blocks_the_consent_nor_hides_reverse_blocked()
    {
        await using var kit = await DriverKit.CreateAsync(fixture, sweeperFactory: FailingSweeper);
        await ToReverseWithMarkersAsync(kit, 2);

        for (var n = 0; n < 9; n++)
        {
            await kit.StepAsync(Ten);
        }

        (await kit.StateAsync()).State.Should().Be("S1");
        kit.Alerts.Should().Contain(a => a.Code == "retire_sweep_failed");
        kit.Alerts.Should().Contain(("reverse_blocked", "retire:2"));
        kit.Alerts.Should().NotContain(a => a.Code == "step_failed");
        (await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.RetireMarkers, "the sweep cannot delete", "op", CancellationToken.None)).Should().Be(ApproveResult.Stored);

        var moving = await kit.StepUntilAsync("S3", "rev", advance: Ten);

        kit.Alerts.Should().Contain(("gate_override", ApprovalKinds.RetireMarkers));
        moving.RetiringGeneration.Should().BeEmpty();
    }

    [Fact]
    public async Task The_retire_sweep_helps_the_marked_copies_away_and_the_retire_step_then_passes()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var (_, ls) = await ToReverseWithMarkersAsync(kit, 3);

        var moving = await kit.StepUntilAsync("S3", "rev", advance: Ten);

        moving.RetiringGeneration.Should().BeEmpty();
        foreach (var l in ls)
        {
            (await kit.Kit.ControlDb.KeyExistsAsync(l)).Should().BeFalse("the superseded copy is deleted before the reverse is served");
        }

        (await Markers(kit)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_stored_consent_skips_the_gate_is_logged_and_leaves_the_markers_obsolete_for_SMGC()
    {
        await using var kit = await DriverKit.CreateAsync(fixture, retireSweeper: false);
        var (_, ls) = await ToReverseWithMarkersAsync(kit, 2);
        (await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.RetireMarkers, "the markers may stay", "op", CancellationToken.None)).Should().Be(ApproveResult.Stored);

        var moving = await kit.StepUntilAsync("S3", "rev", advance: Ten);

        kit.Alerts.Should().Contain(("gate_override", ApprovalKinds.RetireMarkers));
        moving.RetiringGeneration.Should().BeEmpty();
        var left = await Markers(kit);
        left.Should().HaveCount(2);
        foreach (var (field, value) in left)
        {
            (await kit.Kit.Scripts.SupersessionGcAsync(kit.Kit.Names.Control(kit.Group, 0), field, value)).Should().Be(1, "after the retire gate is passed the binding has ended, so the marker is obsolete");
        }

        foreach (var l in ls)
        {
            (await kit.Kit.ControlDb.KeyExistsAsync(l)).Should().BeTrue();
        }

        (await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.RetireMarkers, "late", "op", CancellationToken.None)).Should().Be(ApproveResult.NotApplicable);
    }

    [Fact]
    public async Task The_consent_is_bound_to_the_identity_of_the_reverse_start_it_was_given_under()
    {
        await using var kit = await DriverKit.CreateAsync(fixture, retireSweeper: false);
        await ToReverseWithMarkersAsync(kit, 1);
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.RetireMarkers, "ok", "op", CancellationToken.None);
        var reverse = await kit.StateAsync();
        Approval.ValidFor(reverse, ApprovalKinds.RetireMarkers).Should().NotBeNull();

        var moving = await kit.StepUntilAsync("S3", "rev", advance: Ten);

        Approval.ValidFor(moving, ApprovalKinds.RetireMarkers).Should().BeNull();
        moving.Id.Should().NotBe(reverse.Id);
    }

    [Fact]
    public async Task The_discovery_resumes_at_its_cursor_each_attempt_and_ends_with_a_pass_that_found_nothing()
    {
        await using var kit = await DriverKit.CreateAsync(fixture, retireSweeper: false);
        var (generation, _) = await ToReverseWithMarkersAsync(kit, 0);
        var record = kit.Kit.Names.Control(kit.Group, 0);
        await kit.Kit.ControlDb.HashSetAsync(record, [.. Enumerable.Range(0, 1500).Select(n => new HashEntry($"sm:other-{n}", "some-old-generation|n"))]);
        var gate = new RetireGate(kit.Kit.ControlDb, kit.Kit.Names, new KeyMoveStateOptions { RetireDiscoveryBudget = TimeSpan.Zero }, kit.Kit.Time);
        var state = await kit.StateAsync();

        var attempts = 0;
        RetireVerdict verdict;
        do
        {
            attempts++;
            verdict = await gate.CheckAsync(kit.Group, state, CancellationToken.None);
        }
        while (!verdict.Clear && attempts < 20);

        verdict.Clear.Should().BeTrue();
        attempts.Should().BeGreaterThan(1, "a page is one attempt when the budget is spent");
        generation.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_pass_that_met_a_marker_of_rg_starts_again_from_the_first_slot()
    {
        await using var kit = await DriverKit.CreateAsync(fixture, retireSweeper: false);
        var (_, _) = await ToReverseWithMarkersAsync(kit, 2);
        var gate = new RetireGate(kit.Kit.ControlDb, kit.Kit.Names, kit.Kit.Options, kit.Kit.Time);
        var state = await kit.StateAsync();

        var first = await gate.CheckAsync(kit.Group, state, CancellationToken.None);
        foreach (var (field, value) in await Markers(kit))
        {
            await kit.Kit.Scripts.UnmarkAsync(kit.Kit.Names.Control(kit.Group, 0), field, value);
        }

        var second = await gate.CheckAsync(kit.Group, state, CancellationToken.None);

        first.Should().Be(new RetireVerdict(false, 2));
        second.Clear.Should().BeTrue();
    }

    [Fact]
    public async Task The_start_waits_with_start_blocked_naming_the_nodes_until_every_fresh_node_advertises_the_binding_build()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        kit.Fleet.Nodes[1].Binding = false;

        for (var n = 0; n < 4; n++)
        {
            await kit.StepAsync(Ten);
        }

        (await kit.StateAsync()).State.Should().Be("S0");
        kit.Alerts.Should().Contain(a => a.Code == "start_blocked" && a.Detail.Contains("binding:n2", StringComparison.Ordinal));
        kit.Fleet.Nodes[1].Binding = true;
        await kit.StepUntilAsync("S1");
    }

    [Fact]
    public async Task A_marker_whose_binding_is_neither_fg_nor_rg_deletes_nothing_and_after_the_retire_gate_no_old_marker_reaches_L()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var drained = await ToDrainedAsync(kit);
        var l = "gen-l-" + Guid.NewGuid().ToString("N");
        var record = kit.Kit.Names.Control(kit.Group, 0);
        await kit.Kit.ControlDb.StringSetAsync(l, "forward-copy");
        var fwd = await kit.StateAsync();

        (await kit.Kit.Scripts.SourceDeleteGuardedAsync(l, record, fwd.Id, "neither")).Should().Be(MoveScripts.BindingEnded);
        (await kit.Kit.ControlDb.KeyExistsAsync(l)).Should().BeTrue();
        kit.Fleet.All(n => n.Fl = 2);
        var reverse = await kit.StepUntilAsync("S1", "rev", advance: Ten);
        (await kit.Kit.Scripts.SourceDeleteGuardedAsync(l, record, reverse.Id, "neither")).Should().Be(MoveScripts.BindingEnded);
        await kit.StepUntilAsync("S3", "rev");
        var current = await kit.StateAsync();

        // A reverse writer put a legitimate value at L; a unit that started in S3 holds the old identity, and one that read the state late holds the new one.
        await kit.Kit.ControlDb.StringSetAsync(l, "written-by-the-reverse");
        (await kit.Kit.Scripts.SourceDeleteGuardedAsync(l, record, fwd.Id, drained.ForwardGeneration)).Should().Be(MoveScripts.IdentityMismatch);
        (await kit.Kit.Scripts.SourceDeleteGuardedAsync(l, record, current.Id, drained.ForwardGeneration)).Should().Be(MoveScripts.BindingEnded);

        (await kit.Kit.ControlDb.StringGetAsync(l)).Should().Be("written-by-the-reverse");
    }

    // A sweeper whose deletions fail: the script of every guarded delete throws.
    private static MarkerSweeper FailingSweeper(StateKit kit)
    {
        var db = InterceptingProxy.Wrap(kit.Db, (method, _) => method.Name == nameof(IDatabase.ScriptEvaluateAsync) ? throw new TimeoutException("the deletion was refused") : InterceptingProxy.Proceed);
        var version = kit.Mux.GetServers().Where(s => !s.IsReplica).Select(s => s.Version).Min() ?? new Version(7, 0);
        return new MarkerSweeper(kit.ControlDb, new MoveScripts(db, useScripts: true, version), kit.Names, kit.Options, kit.Time);
    }

    // The group in S1(rev) with markers of the retiring generation, each with its copy of L.
    private static async Task<(string Generation, List<string> Ls)> ToReverseWithMarkersAsync(DriverKit kit, int markers)
    {
        var drained = await ToDrainedAsync(kit);
        kit.Fleet.All(n => n.Fl = 2);
        await kit.StepUntilAsync("S1", "rev", advance: Ten);
        var record = kit.Kit.Names.Control(kit.Group, 0);
        var ls = Enumerable.Range(0, markers).Select(n => $"retire-l-{n}-{Guid.NewGuid():N}").ToList();
        foreach (var l in ls)
        {
            await kit.Kit.ControlDb.StringSetAsync(l, "superseded");
            await kit.Kit.ControlDb.HashSetAsync(record, "sm:" + l, drained.ForwardGeneration + "|n");
        }

        return (drained.ForwardGeneration, ls);
    }

    private static async Task<StateRecord> ToDrainedAsync(DriverKit kit)
    {
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S3");
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        kit.Mover.Inventory = new Dictionary<string, long> { ["a"] = 0 };
        return await kit.StepUntilAsync("S4");
    }

    private static async Task<List<(string Field, string Value)>> Markers(DriverKit kit) =>
        [.. (await kit.Kit.ControlAsync(0)).Where(e => e.Name.ToString().StartsWith("sm:", StringComparison.Ordinal)).Select(e => (e.Name.ToString(), e.Value.ToString()))];
}
