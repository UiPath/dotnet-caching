using System.Globalization;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class KeyInventoryTests(RedisContainerFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_inventory_counts_owned_source_keys_per_cache_with_a_zero_for_a_cache_that_holds_none()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        await kit.SetAsync(MoveSide.Legacy, "a", "k1");
        await kit.SetAsync(MoveSide.Legacy, "a", "mv2");
        await kit.SetAsync(MoveSide.Legacy, "c", "k3");
        await kit.SetAsync(MoveSide.Native, "b", "k4");

        var inventory = (await kit.Inventory.RunAsync(kit.Group, Ct))!;

        inventory.Residue.Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 2, ["b"] = 0 });
        inventory.Movable.Should().Be(1);
    }

    [Fact]
    public async Task A_reverse_inventory_counts_native_keys_without_measuring_a_canary()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        using var counted = new KeyMoveMeterRecorder("cache.keymove.canary_keys", kit.Group);
        kit.Space.MaxTtl["b"] = TimeSpan.FromMinutes(10);
        await kit.SetAsync(MoveSide.Native, "b", "k1", TimeSpan.FromHours(1));
        await kit.SetAsync(MoveSide.Native, "b", "mv2");
        await kit.SetAsync(MoveSide.Legacy, "b", "k2");
        kit.Kit.Time.Advance(TimeSpan.FromMinutes(1));
        var state = await kit.MoveToAsync("S4", "rev", 1);
        var t3 = kit.Kit.Time.GetUtcNow().ToUnixTimeMilliseconds();
        (await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, t3.ToString(CultureInfo.InvariantCulture)), (StateFields.AllAck, state.Id)], [])).Should().Be(1);

        var inventory = (await kit.Inventory.RunAsync(kit.Group, Ct))!;

        inventory.Residue["b"].Should().Be(2);
        inventory.Movable.Should().Be(1);
        KeyInventory.SourceOf(state).Should().Be(MoveSide.Native);
        inventory.Canary.Should().BeNull();
        counted.Measured.Should().BeEmpty();
    }

    [Fact]
    public async Task The_canary_counts_keys_whose_life_says_they_were_written_after_S3_and_needs_the_time_of_S3()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        using var counted = new KeyMoveMeterRecorder("cache.keymove.canary_keys", kit.Group);
        kit.Kit.Time.Advance(TimeSpan.FromDays(1));
        kit.Space.MaxTtl["a"] = TimeSpan.FromMinutes(10);
        await kit.SetAsync(MoveSide.Legacy, "a", "long", TimeSpan.FromHours(1));
        await kit.SetAsync(MoveSide.Legacy, "a", "longer", TimeSpan.FromHours(2));
        await kit.SetAsync(MoveSide.Legacy, "a", "short", TimeSpan.FromSeconds(30));
        await kit.SetAsync(MoveSide.Legacy, "a", "never");
        (await kit.Inventory.RunAsync(kit.Group, Ct))!.Canary.Should().BeNull("no S3 time, no evidence");

        var state = await kit.StateAsync();
        var t3 = kit.Kit.Time.GetUtcNow().AddMinutes(-2).ToUnixTimeMilliseconds();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, t3.ToString(CultureInfo.InvariantCulture)), (StateFields.AllAck, state.Id)], []);

        (await kit.Inventory.RunAsync(kit.Group, Ct))!.Canary.Should().Be(2, "both long-lived keys exceed the source life bound");
        counted.Measured.Select(m => m.Value).Should().Equal(2L);
        (await kit.Inventory.RunAsync(kit.Group, Ct))!.Canary.Should().Be(2);
        counted.Measured.Select(m => m.Value).Should().Equal([2L, 2L], "each pass counts its canary keys, and a pass with no evidence counts none");
    }

    [Fact]
    public async Task The_drain_report_never_passes_without_a_mover_pass_and_then_needs_nothing_movable()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var state = await kit.StateAsync();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, "1"), (StateFields.AllAck, state.Id)], []);
        state = await kit.StateAsync();
        await kit.SetAsync(MoveSide.Legacy, "a", "k1");

        (await new InventoryMoverStatus(kit.Inventory, kit.Kit.Time).DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeFalse("only the mover can say its pass finished");
        (await new InventoryMoverStatus(kit.Inventory, kit.Kit.Time, (_, _, _) => Task.FromResult(true)).DrainAsync(kit.Group, state, Ct))!.Should().Match<DrainReport>(r => r.Passed && r.Residue["a"] == 1 && r.Canary == 0);
        await kit.SetAsync(MoveSide.Legacy, "a", "mv2");
        (await new InventoryMoverStatus(kit.Inventory, kit.Kit.Time, (_, _, _) => Task.FromResult(true)).DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeFalse("a movable key is left");
    }

    // A value the codec refuses for good (a null N does not cache, one above the cap) is dropped by a delete: it stays in the residue and is counted apart, so it does not keep the drain from passing.
    // The verdict comes from the stored length alone: no value is read.
    [Fact]
    public async Task A_droppable_value_stays_in_the_residue_is_counted_apart_does_not_block_the_drain_and_is_never_read()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var state = await kit.StateAsync();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, "1"), (StateFields.AllAck, state.Id)], []);
        state = await kit.StateAsync();
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var spy = InterceptingProxy.Wrap(kit.Kit.Db, (method, _) =>
        {
            calls.Enqueue(method.Name);
            return InterceptingProxy.Proceed;
        });
        var lengths = new System.Collections.Concurrent.ConcurrentBag<long>();
        var inventory = new KeyInventory(kit.Scanner, kit.Space, kit.Kit.Store, spy, kit.Prefix, kit.Kit.Time, values: new DropsAtLength(lengths));
        var movable = await kit.SetAsync(MoveSide.Legacy, "a", "mv1");
        await kit.SetAsync(MoveSide.Legacy, "a", "mv2", value: string.Empty);
        await kit.SetAsync(MoveSide.Legacy, "a", "mv3", value: new string('x', 11));
        var status = new InventoryMoverStatus(inventory, kit.Kit.Time, (_, _, _) => Task.FromResult(true));

        var before = (await inventory.RunAsync(kit.Group, Ct))!;
        var blocked = await status.DrainAsync(kit.Group, state, Ct);
        await kit.Kit.Db.KeyDeleteAsync(kit.Prefix + movable);
        status = new InventoryMoverStatus(inventory, kit.Kit.Time, (_, _, _) => Task.FromResult(true));
        var drained = await status.DrainAsync(kit.Group, state, Ct);

        before.Should().Match<InventoryResult>(r => r.Movable == 1 && r.Droppable == 2 && r.Residue["a"] == 3);
        blocked!.Passed.Should().BeFalse("a movable key is left");
        drained!.Should().Match<DrainReport>(r => r.Passed && r.Residue["a"] == 2, "only droppable keys remain, and they are still the residue");
        calls.Should().NotContain(name => name.StartsWith("StringGet", StringComparison.Ordinal), "a value is never fetched to be classified");
        calls.Should().Contain("StringLengthAsync");
        lengths.Should().Contain([0L, 1L, 11L]);
    }

    [Fact]
    public async Task A_scan_with_no_classifier_asks_for_no_length_and_no_value()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var spy = InterceptingProxy.Wrap(kit.Kit.Db, (method, _) =>
        {
            calls.Enqueue(method.Name);
            return InterceptingProxy.Proceed;
        });
        var inventory = new KeyInventory(kit.Scanner, kit.Space, kit.Kit.Store, spy, kit.Prefix, kit.Kit.Time);
        await kit.SetAsync(MoveSide.Legacy, "a", "mv1");

        (await inventory.RunAsync(kit.Group, Ct))!.Residue["a"].Should().Be(1);

        calls.Should().NotContain(name => name.StartsWith("StringGet", StringComparison.Ordinal) || name.StartsWith("StringLength", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Drain_confirmation_requires_an_inventory_started_after_the_replica_lag_window()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var state = await kit.StateAsync();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, "1"), (StateFields.AllAck, state.Id)], []);
        state = await kit.StateAsync();
        await kit.SetAsync(MoveSide.Legacy, "a", "k1");
        var clean = true;
        var status = new InventoryMoverStatus(kit.Inventory, kit.Kit.Time, (_, _, _) => Task.FromResult(clean), TimeSpan.FromSeconds(10));

        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeFalse("the passes were just seen");
        kit.Kit.Time.Advance(TimeSpan.FromSeconds(9));
        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeFalse("not a replica lag yet");
        kit.Kit.Time.Advance(TimeSpan.FromSeconds(2));
        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeTrue("the inventory cached before the wait is not the confirmation, and a fresh one agrees");

        clean = false;
        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeFalse();
        clean = true;
        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeFalse("the wait starts again when the passes stop being clean");
    }

    [Fact]
    public async Task A_new_state_id_restarts_confirmation_when_the_mover_stays_clean()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var state = (await kit.StateAsync()) with { Direction = "rev" };
        var wait = TimeSpan.FromSeconds(10);
        var status = new InventoryMoverStatus(kit.Inventory, kit.Kit.Time, (_, _, _) => Task.FromResult(true), wait);

        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeFalse();
        kit.Kit.Time.Advance(wait);
        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeTrue();

        var next = state with { Id = "next" };
        (await status.DrainAsync(kit.Group, next, Ct))!.Passed.Should().BeFalse();
        kit.Kit.Time.Advance(wait - TimeSpan.FromSeconds(1));
        (await status.DrainAsync(kit.Group, next, Ct))!.Passed.Should().BeFalse();
        kit.Kit.Time.Advance(TimeSpan.FromSeconds(1));
        (await status.DrainAsync(kit.Group, next, Ct))!.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Changed_evidence_restarts_the_wait_and_invalidates_the_previous_inventory()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var state = await kit.StateAsync();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, "1"), (StateFields.AllAck, state.Id)], []);
        state = await kit.StateAsync();
        await kit.SetAsync(MoveSide.Legacy, "a", "k1");
        string? evidence = "topology-one";
        var status = new InventoryMoverStatus(kit.Inventory, kit.Kit.Time, null, TimeSpan.FromSeconds(10), (_, _, _) => Task.FromResult<string?>(evidence));

        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeFalse("the passes were just seen");
        kit.Kit.Time.Advance(TimeSpan.FromSeconds(9));
        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeFalse("not a replica lag yet");

        // A primary joined and finished its pass between two looks: the passes are the same as far as the count of a clean group goes, but they are not the ones the wait began for.
        evidence = "topology-two";
        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeFalse("the evidence changed, so the wait begins again");
        kit.Kit.Time.Advance(TimeSpan.FromSeconds(2));
        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeFalse("only two seconds since the new evidence, though eleven since the first");
        kit.Kit.Time.Advance(TimeSpan.FromSeconds(9));
        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeTrue("a replica lag after the new evidence was first seen");

        evidence = null;
        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeFalse("no evidence");
    }

    [Fact]
    public async Task A_forward_report_is_null_without_the_time_of_S3_and_the_residue_inventory_still_answers()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        await kit.SetAsync(MoveSide.Legacy, "b", "k1");
        var status = new InventoryMoverStatus(kit.Inventory, kit.Kit.Time);

        (await status.DrainAsync(kit.Group, await kit.StateAsync(), Ct)).Should().BeNull();
        (await status.ResidueInventoryAsync(kit.Group, Ct))["b"].Should().Be(1);
        (await status.ResidueInventoryAsync("g" + Guid.NewGuid().ToString("N")[..8], Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_time_of_S3_that_no_acknowledgement_of_the_current_identity_stands_behind_is_no_time()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        kit.Space.MaxTtl["a"] = TimeSpan.FromMinutes(10);
        var state = await kit.StateAsync();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, "1"), (StateFields.AllAck, "an-older-identity")], []);

        (await kit.Inventory.RunAsync(kit.Group, Ct))!.Canary.Should().BeNull("the time is an older move's");
    }

    [Fact]
    public async Task The_drain_report_reuses_one_inventory_for_thirty_seconds_and_asks_the_mover_first()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var state = await kit.StateAsync();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, "1"), (StateFields.AllAck, state.Id)], []);
        state = await kit.StateAsync();
        await kit.SetAsync(MoveSide.Legacy, "a", "k1");
        var events = new List<string>();
        kit.Space.OnOwnerOf = _ => events.Add("scan");
        var status = new InventoryMoverStatus(kit.Inventory, kit.Kit.Time, (_, _, _) =>
        {
            events.Add("mover");
            return Task.FromResult(false);
        });

        await status.DrainAsync(kit.Group, state, Ct);
        kit.Kit.Time.Advance(TimeSpan.FromSeconds(1));
        await status.DrainAsync(kit.Group, state, Ct);
        events.Count(e => e == "scan").Should().Be(1, "the second report reuses the inventory");
        events[0].Should().Be("mover");
        kit.Kit.Time.Advance(TimeSpan.FromSeconds(31));
        await status.DrainAsync(kit.Group, state, Ct);

        events.Count(e => e == "scan").Should().Be(2);
    }

    [Fact]
    public async Task Acknowledging_the_same_state_id_rescans_inside_the_reuse_window()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var state = await kit.StateAsync();
        kit.Space.MaxTtl["a"] = TimeSpan.FromMinutes(10);
        await kit.SetAsync(MoveSide.Legacy, "a", "long", TimeSpan.FromHours(1));
        var status = new InventoryMoverStatus(kit.Inventory, kit.Kit.Time);

        (await status.DrainAsync(kit.Group, state, Ct)).Should().BeNull();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, "1"), (StateFields.AllAck, state.Id)], []);
        kit.Kit.Time.Advance(TimeSpan.FromSeconds(1));

        var acknowledged = await kit.StateAsync();
        acknowledged.Id.Should().Be(state.Id);
        var report = await status.DrainAsync(kit.Group, acknowledged, Ct);
        report.Should().Match<DrainReport>(r => r.Canary == 1);
    }

    [Fact]
    public async Task Changing_direction_rescans_the_new_source_inside_the_reuse_window()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var state = await kit.StateAsync();
        state = state with { AllAck = state.Id, Values = new Dictionary<string, string> { [StateFields.T3At] = "1" } };
        await kit.SetAsync(MoveSide.Legacy, "a", "legacy");
        await kit.SetAsync(MoveSide.Native, "b", "native");
        var status = new InventoryMoverStatus(kit.Inventory, kit.Kit.Time);

        (await status.DrainAsync(kit.Group, state, Ct))!.Residue["a"].Should().Be(1);
        kit.Kit.Time.Advance(TimeSpan.FromSeconds(1));

        var reversed = await status.DrainAsync(kit.Group, state with { Direction = "rev" }, Ct);
        reversed!.Residue.Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 0, ["b"] = 1 });
    }

    [Fact]
    public async Task Changing_state_id_rescans_the_same_source_inside_the_reuse_window()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var state = (await kit.StateAsync()) with { Direction = "rev" };
        await kit.SetAsync(MoveSide.Native, "b", "first");
        var status = new InventoryMoverStatus(kit.Inventory, kit.Kit.Time);

        (await status.DrainAsync(kit.Group, state, Ct))!.Residue["b"].Should().Be(1);
        await kit.SetAsync(MoveSide.Native, "b", "second");
        kit.Kit.Time.Advance(TimeSpan.FromSeconds(1));

        var next = await status.DrainAsync(kit.Group, state with { Id = "next" }, Ct);
        next!.Residue["b"].Should().Be(2);
    }

    [Fact]
    public async Task A_inventory_that_fails_on_Redis_is_no_evidence_for_the_drain_and_an_error_for_the_residue()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var state = await kit.StateAsync();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, "1"), (StateFields.AllAck, state.Id)], []);
        state = await kit.StateAsync();
        kit.Kit.Topology.Layout = (_, _) => throw new RedisException("the topology is unreadable");
        var status = new InventoryMoverStatus(kit.Inventory, kit.Kit.Time);

        (await status.DrainAsync(kit.Group, state, Ct)).Should().BeNull();
        var act = () => status.ResidueInventoryAsync(kit.Group, Ct);
        await act.Should().ThrowAsync<RedisException>();
    }

    [Fact]
    public async Task A_completed_mover_pass_with_only_invalid_mappings_left_cannot_confirm_the_drain()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var state = await kit.StateAsync();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, "1"), (StateFields.AllAck, state.Id)], []);
        state = await kit.StateAsync();
        kit.Space.InvalidDestination = k => k.Contains(":bad", StringComparison.Ordinal);
        var status = new InventoryMoverStatus(kit.Inventory, kit.Kit.Time, (_, _, _) => Task.FromResult(true));
        await kit.SetAsync(MoveSide.Legacy, "a", "skipped-class");

        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeTrue("a key in an accepted skip class does not block");
        await kit.SetAsync(MoveSide.Legacy, "a", "bad1");
        kit.Kit.Time.Advance(InventoryMoverStatus.ReuseFor + TimeSpan.FromSeconds(1));

        var inventory = (await kit.Inventory.RunAsync(kit.Group, Ct))!;
        inventory.InvalidMapping.Should().Be(1);
        inventory.Movable.Should().Be(0, "nothing is movable, and the mover pass is complete");
        (await status.DrainAsync(kit.Group, state, Ct))!.Passed.Should().BeFalse("an invalid mapping is a defect, not a skip class");
    }

    private sealed class DropsAtLength(System.Collections.Concurrent.ConcurrentBag<long> seen) : IValueClassifier
    {
        public bool IsDroppable(string cacheId, MoveSide side, long length)
        {
            seen.Add(length);
            return length == 0 || length > 10;
        }
    }
}
