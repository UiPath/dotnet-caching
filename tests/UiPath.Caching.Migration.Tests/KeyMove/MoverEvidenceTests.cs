using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The confirmation window belongs to one identity and topology.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoverEvidenceTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_evidence_changes_with_the_identity_the_primaries_and_the_run_id_of_each()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var real = (await kit.Units.ListAsync(Ct)).Single();
        var one = real with { Id = "u1", Primary = ScanRecordingServer.Over(real.Primary, out var first) };
        var two = real with { Id = "u2", Primary = ScanRecordingServer.Over(real.Primary, out var second) };
        first.RunId = "run-a";
        second.RunId = "run-b";
        var listed = new List<ScanUnit> { one };
        var state = kit.State();
        var passes = new MoverPasses(new Listed(() => listed), kit.Db, kit.Kit.Names, kit.Space, (_, _) => Task.FromResult<StateRecord?>(state));
        async Task RecordAsync(string identity)
        {
            foreach (var id in new[] { "u1", "u2" })
            {
                await kit.Db.StringSetAsync(kit.Kit.Names.UnitPass(kit.Kit.Group, id, "fwd", MoverKit.MoveId), PassRecord.Text(identity, MoverKit.MoveId, 1, 0, "ep#" + id, MoveScan.HashOf(MoveScan.For(kit.Space, kit.Kit.Group))), flags: StackExchange.Redis.CommandFlags.DemandMaster);
            }
        }

        Task<string?> Evidence() => passes.EvidenceAsync(kit.Kit.Group, "fwd", Ct);
        await RecordAsync(kit.Kit.Id);
        var baseline = await Evidence();

        baseline.Should().NotBeNull();
        (await Evidence()).Should().Be(baseline, "nothing changed");
        (await passes.FinishedAfterAckAsync(kit.Kit.Group, "fwd", Ct)).Should().BeTrue();

        first.RunId = "run-restarted";
        var restarted = await Evidence();
        restarted.Should().NotBeNull().And.NotBe(baseline, "the primary's run id changed");
        first.RunId = "run-a";

        listed.Add(two);
        var added = await Evidence();
        added.Should().NotBeNull().And.NotBe(baseline, "a primary was added");
        listed.Remove(one);
        var removed = await Evidence();
        removed.Should().NotBeNull().And.NotBe(baseline).And.NotBe(added, "a primary was removed");
        listed.Remove(two);
        listed.Add(one);

        state = state with { Id = "newer", AllAck = "newer" };
        await RecordAsync("newer");
        var renewed = await Evidence();
        renewed.Should().NotBeNull().And.NotBe(baseline, "another identity");

        first.Info = InfoFault.Fail;
        (await Evidence()).Should().BeNull("a run id that cannot be read is no evidence");
    }

    [Fact]
    public async Task A_pass_from_a_string_only_scan_plan_cannot_confirm_a_hash_group()
    {
        await using var kit = await MoverKit.CreateAsync(fixture, hashCache: true);
        await kit.SeedAsync("k");
        await kit.RunOnePassAsync(kit.Host());
        var group = kit.Kit.Group;
        var unit = (await kit.UnitIdsAsync()).Single();
        var key = kit.Kit.Names.UnitPass(group, unit, "fwd", MoverKit.MoveId);
        var current = (await kit.Db.StringGetAsync(key)).ToString();
        var source = PassRecord.SourceOf(current);
        var scalarPlan = MoveScan.HashOf(MoveScan.For(kit.Space, group).Where(scan => scan.Kind == KeyMoveKind.String).ToArray());
        var passes = new MoverPasses(kit.Units, kit.Db, kit.Kit.Names, kit.Space, (_, _) => Task.FromResult<StateRecord?>(kit.State()));

        await kit.Db.StringSetAsync(key, $"{kit.Kit.Id}|{MoverKit.MoveId}|1|0|{source}");
        (await passes.FinishedAfterAckAsync(group, "fwd", Ct)).Should().BeFalse();
        await kit.Db.StringSetAsync(key, PassRecord.Text(kit.Kit.Id, MoverKit.MoveId, 1, 0, source, scalarPlan));
        (await passes.FinishedAfterAckAsync(group, "fwd", Ct)).Should().BeFalse();
        await kit.Db.StringSetAsync(key, current);
        (await passes.FinishedAfterAckAsync(group, "fwd", Ct)).Should().BeTrue();
    }

    private sealed class Listed(Func<IReadOnlyList<ScanUnit>> units) : IScanUnits
    {
        public Task<IReadOnlyList<ScanUnit>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(units());
    }
}
