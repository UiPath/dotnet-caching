using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

// The generation lifecycle on a cluster: where the records of a rebuilt state come from, and the retire discovery across the slots of several masters.
[Trait("Category", "Integration")]
public class GenerationClusterTests
{
    [Fact]
    public async Task Reassert_takes_the_generations_of_the_committed_record_and_an_uncommitted_reverse_start_with_a_higher_ep_hands_over_no_rg()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true);
        await kit.InitAsync("a");
        var committed = new ControlRecord(5, "committed-id", "S3", "fwd", "1", "S1", "fwd", "0", "4", "earlier-id", [("a", "g1")]) { BindingField = "fg", ForwardGeneration = "gen-c" };
        var uncommitted = new ControlRecord(6, "reverse-start-id", "S1", "rev", "1", "S3", "fwd", "1", "5", "committed-id", [("a", "g1")]) { BindingField = "fg", RetiringGeneration = "gen-c" };
        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 0), committed);
        await kit.Scripts.ControlCommitAsync(kit.Names.Control(kit.Group, 0), 5, "committed-id");
        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 1), uncommitted);
        await kit.Db.KeyDeleteAsync(kit.StateKey);

        var result = await kit.Recovery.ReassertAsync(kit.Group, "state lost", null, null, null, CancellationToken.None);
        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;

        result.Outcome.Should().Be(RecoveryOutcome.Done);
        state.State.Should().Be("S3");
        state.Direction.Should().Be("fwd");
        state.ForwardGeneration.Should().Be("gen-c");
        state.RetiringGeneration.Should().BeEmpty();
    }

    [Fact]
    public async Task Reassert_in_S1_reverse_keeps_rg_of_the_committed_record_when_an_uncommitted_retire_step_has_a_higher_ep()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true);
        var committed = new ControlRecord(5, "reverse-id", "S1", "rev", "1", "S4", "fwd", "1", "4", "earlier-id", [("a", "g1")]) { BindingField = "fg", RetiringGeneration = "gen-c" };
        var uncommitted = new ControlRecord(6, "retire-id", "S3", "rev", "1", "S1", "rev", "1", "5", "reverse-id", [("a", "g1")]) { BindingField = "fg" };
        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 0), committed);
        await kit.Scripts.ControlCommitAsync(kit.Names.Control(kit.Group, 0), 5, "reverse-id");
        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 1), uncommitted);

        var result = await kit.Recovery.ReassertAsync(kit.Group, "state lost", null, null, null, CancellationToken.None);
        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;

        result.Outcome.Should().Be(RecoveryOutcome.Done);
        state.State.Should().Be("S1");
        state.Direction.Should().Be("rev");
        state.RetiringGeneration.Should().Be("gen-c", "the gate stays closed for the markers of the committed S1(rev)");
        state.ForwardGeneration.Should().BeEmpty();
    }

    [Fact]
    public async Task Recreate_never_hands_the_rg_of_an_uncommitted_record_to_the_rebuilt_state()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true);
        var committed = new ControlRecord(5, "reverse-id", "S1", "rev", "1", "S4", "fwd", "1", "4", "earlier-id", [("a", "g1")]) { BindingField = "fg", RetiringGeneration = "gen-c" };
        var uncommitted = new ControlRecord(6, "retire-id", "S3", "rev", "1", "S1", "rev", "1", "5", "reverse-id", [("a", "g1")]) { BindingField = "fg" };
        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 0), committed);
        await kit.Scripts.ControlCommitAsync(kit.Names.Control(kit.Group, 0), 5, "reverse-id");
        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 1), uncommitted);

        var result = await kit.Recovery.RecreateAsync(kit.Group, [new PeerIdentity("S1", "rev", 5, "reverse-id")], ["a"], null, CancellationToken.None);
        var state = await kit.Store.ReadAsync(kit.Group, CancellationToken.None);

        // Either the records are refused as disagreeing, or the state that was rebuilt keeps the committed record's rg; it never loses it to the higher ep.
        (result.Outcome == RecoveryOutcome.RecordsDisagree || state!.RetiringGeneration == "gen-c").Should().BeTrue();
    }

    [Fact]
    public async Task The_retire_discovery_resumes_at_its_cursor_across_the_slots_of_every_master_and_finishes()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true);
        var scanned = new List<string>();
        var database = InterceptingProxy.Wrap(kit.ControlDb, (method, args) =>
        {
            if (method.Name == nameof(IDatabase.ExecuteAsync) && args is [string command, IEnumerable<object> arguments, ..] && command == "HSCAN")
            {
                lock (scanned)
                {
                    scanned.Add(((RedisKey)arguments.First()).ToString());
                }
            }

            return InterceptingProxy.Proceed;
        });

        // Markers of an older generation, not the retiring one, in slots of each master: several pages each. The slots with no marker are skipped by their length.
        int[] withMarkers = [3, 6000, 12000, 16000];
        foreach (var slot in withMarkers)
        {
            await kit.ControlDb.HashSetAsync(kit.Names.Control(kit.Group, slot), [.. Enumerable.Range(0, 1200).Select(n => new HashEntry($"sm:old-{n}", "an-older-generation|n"))]);
        }

        var gate = new RetireGate(database, kit.Names, new KeyMoveStateOptions { RetireDiscoveryBudget = TimeSpan.Zero }, TimeProvider.System);
        var state = new StateRecord("S1", "rev", 9, "reverse-start-id", true, 1, string.Empty, null, "a", "cs", string.Empty, string.Empty, string.Empty, string.Empty, Values: new Dictionary<string, string> { [StateFields.Rg] = "gen-c" });

        var attempts = 0;
        RetireVerdict verdict;
        do
        {
            attempts++;
            verdict = await gate.CheckAsync(kit.Group, state, CancellationToken.None);
        }
        while (!verdict.Clear && attempts < 200);

        verdict.Clear.Should().BeTrue();
        attempts.Should().BeGreaterThan(1, "an attempt that runs out of its budget mid-scan is continued by the next");
        var order = scanned.Distinct().ToList();
        order.Should().HaveCount(withMarkers.Length, "every slot that holds fields beyond its fixed ones, on every master, was scanned");
        scanned.Should().Equal(scanned.OrderBy(k => order.IndexOf(k)), "no slot is scanned again after the scan moved on: the next attempt resumes where the last stopped");
    }
}
