using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class MarkerSweeperTests(RedisContainerFixture fixture)
{
    private const string Id = "idA";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_obsolete_marker_is_removed_and_a_current_one_is_not_touched_while_its_writer_may_still_run(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var (sweeper, state, l) = await StartAsync(kit);
        var record = kit.Names.Control(kit.Group, 0);
        await kit.ControlDb.HashSetAsync(record, [new("sm:old", "idOld|n"), new("sm:" + l, Id + "|n")]);

        var result = await sweeper.SweepAsync(kit.Group, state, SweepMode.Orphans, CancellationToken.None);

        result.Obsolete.Should().Be(1);
        result.Helped.Should().Be(0);
        (await kit.ControlDb.HashExistsAsync(record, "sm:old")).Should().BeFalse();
        (await kit.ControlDb.HashExistsAsync(record, "sm:" + l)).Should().BeTrue();
        (await kit.ControlDb.StringGetAsync(l)).Should().Be("older");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_current_marker_whose_writer_is_gone_is_helped_the_copy_is_deleted_and_the_marker_removed(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var (sweeper, state, l) = await StartAsync(kit);
        var record = kit.Names.Control(kit.Group, 0);
        await kit.ControlDb.HashSetAsync(record, "sm:" + l, Id + "|n");
        await sweeper.SweepAsync(kit.Group, state, SweepMode.Orphans, CancellationToken.None);

        kit.Time.Advance(kit.Options.MarkerOrphanAfter + TimeSpan.FromSeconds(1));
        var result = await sweeper.SweepAsync(kit.Group, state, SweepMode.Orphans, CancellationToken.None);

        result.Helped.Should().Be(1);
        result.Orphans.Should().Be(1);
        (await kit.ControlDb.KeyExistsAsync(l)).Should().BeFalse();
        (await kit.ControlDb.HashExistsAsync(record, "sm:" + l)).Should().BeFalse();
    }

    [Fact]
    public async Task Each_orphan_the_sweeper_handles_is_counted_for_its_group()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var (sweeper, state, l) = await StartAsync(kit);
        var counted = new List<(long Value, string? Group)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l2) =>
        {
            if (instrument.Meter.Name == KeyMoveTelemetry.MeterName && instrument.Name == "cache.keymove.marker_orphans")
            {
                l2.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            lock (counted)
            {
                counted.Add((value, tags.ToArray().Where(t => t.Key == "group").Select(t => t.Value?.ToString()).FirstOrDefault()));
            }
        });
        listener.Start();
        await kit.ControlDb.HashSetAsync(kit.Names.Control(kit.Group, 0), "sm:" + l, Id + "|n");
        await sweeper.SweepAsync(kit.Group, state, SweepMode.Orphans, CancellationToken.None);
        kit.Time.Advance(kit.Options.MarkerOrphanAfter + TimeSpan.FromSeconds(1));

        await sweeper.SweepAsync(kit.Group, state, SweepMode.Orphans, CancellationToken.None);

        lock (counted)
        {
            counted.Where(c => c.Group == kit.Group).Select(c => c.Value).Sum().Should().Be(1);
        }
    }

    [Fact]
    public async Task A_current_marker_whose_copy_is_absent_is_removed_without_a_delete()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var (sweeper, state, l) = await StartAsync(kit);
        var record = kit.Names.Control(kit.Group, 0);
        await kit.ControlDb.KeyDeleteAsync(l);
        await kit.ControlDb.HashSetAsync(record, "sm:" + l, Id + "|n");
        await sweeper.SweepAsync(kit.Group, state, SweepMode.Orphans, CancellationToken.None);

        kit.Time.Advance(kit.Options.MarkerOrphanAfter + TimeSpan.FromSeconds(1));
        var result = await sweeper.SweepAsync(kit.Group, state, SweepMode.Orphans, CancellationToken.None);

        result.Helped.Should().Be(0);
        result.Orphans.Should().Be(1);
        (await kit.ControlDb.HashExistsAsync(record, "sm:" + l)).Should().BeFalse();
    }

    [Fact]
    public async Task A_marker_that_was_replaced_starts_its_age_again()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var (sweeper, state, l) = await StartAsync(kit);
        var record = kit.Names.Control(kit.Group, 0);
        await kit.ControlDb.HashSetAsync(record, "sm:" + l, Id + "|first");
        await sweeper.SweepAsync(kit.Group, state, SweepMode.Orphans, CancellationToken.None);
        kit.Time.Advance(kit.Options.MarkerOrphanAfter + TimeSpan.FromSeconds(1));
        await kit.ControlDb.HashSetAsync(record, "sm:" + l, Id + "|second");

        var result = await sweeper.SweepAsync(kit.Group, state, SweepMode.Orphans, CancellationToken.None);

        result.Orphans.Should().Be(0, "the new write may still be running");
        (await kit.ControlDb.StringGetAsync(l)).Should().Be("older");
    }

    [Fact]
    public async Task A_record_that_holds_another_identity_than_the_state_is_left_alone()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var (sweeper, state, _) = await StartAsync(kit);
        var record = kit.Names.Control(kit.Group, 0);
        await kit.ControlDb.HashSetAsync(record, [new("sm:old", "idOld|n"), new("id", "idB")]);

        var result = await sweeper.SweepAsync(kit.Group, state, SweepMode.Orphans, CancellationToken.None);

        result.Should().Be(default(MarkerSweepResult));
        (await kit.ControlDb.HashExistsAsync(record, "sm:old")).Should().BeTrue();
    }

    [Fact]
    public async Task A_marker_whose_binding_ended_is_not_used_to_delete_a_copy()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var (sweeper, state, l) = await StartAsync(kit);
        var record = kit.Names.Control(kit.Group, 0);

        // The record binds to its forward generation; a marker of the identity is obsolete and goes by SMGC, and the copy is never deleted for it.
        await kit.ControlDb.HashSetAsync(record, [new("bf", "fg"), new("fg", "g9"), new("sm:" + l, Id + "|n")]);
        var result = await sweeper.SweepAsync(kit.Group, state, SweepMode.Orphans, CancellationToken.None);
        kit.Time.Advance(kit.Options.MarkerOrphanAfter + TimeSpan.FromSeconds(1));
        await sweeper.SweepAsync(kit.Group, state, SweepMode.Orphans, CancellationToken.None);

        result.Obsolete.Should().Be(1);
        (await kit.ControlDb.StringGetAsync(l)).Should().Be("older");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_retire_mode_helps_the_markers_of_the_retiring_generation_at_once_and_removes_the_others_by_SMGC(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var record = kit.Names.Control(kit.Group, 0);
        await kit.Scripts.ControlSyncAsync(record, new ControlRecord(2, Id, "S1", "rev", "1", "S3", "fwd", "1", "1", "idOld", [("c1", "g1")]) { Limit = 100, BindingField = "fg", RetiringGeneration = "genA" });
        await kit.ControlDb.HashSetAsync(record, "pk", "2");
        var l = "retire-l-" + Guid.NewGuid().ToString("N");
        await kit.ControlDb.StringSetAsync(l, "superseded");
        await kit.ControlDb.HashSetAsync(record, [new("sm:" + l, "genA|n"), new("sm:older", "genOld|n")]);
        var state = ReverseState();
        var sweeper = new MarkerSweeper(kit.ControlDb, kit.Scripts, kit.Names, kit.Options, kit.Time);

        var result = await sweeper.SweepAsync(kit.Group, state, SweepMode.Retire, CancellationToken.None);

        result.Helped.Should().Be(1);
        result.Obsolete.Should().Be(1);
        (await kit.ControlDb.KeyExistsAsync(l)).Should().BeFalse();
        (await kit.ControlDb.HashExistsAsync(record, "sm:" + l)).Should().BeFalse();
        (await kit.ControlDb.HashExistsAsync(record, "sm:older")).Should().BeFalse();
    }

    [Fact]
    public async Task Each_mode_applies_to_its_own_states_only()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var (sweeper, forward, l) = await StartAsync(kit);
        await kit.ControlDb.HashSetAsync(kit.Names.Control(kit.Group, 0), "sm:" + l, "genA|n");

        MarkerSweeper.Applies(forward, SweepMode.Retire).Should().BeFalse();
        MarkerSweeper.Applies(ReverseState(), SweepMode.Orphans).Should().BeFalse();
        MarkerSweeper.Applies(ReverseState(), SweepMode.Retire).Should().BeTrue();
        (await sweeper.SweepAsync(kit.Group, forward, SweepMode.Retire, CancellationToken.None)).Should().Be(default(MarkerSweepResult));
        (await sweeper.SweepAsync(kit.Group, ReverseState(), SweepMode.Orphans, CancellationToken.None)).Should().Be(default(MarkerSweepResult));
        (await kit.ControlDb.StringGetAsync(l)).Should().Be("older");
    }

    private static StateRecord ReverseState() =>
        new("S1", "rev", 2, Id, true, 1, string.Empty, null, "c1", "cs", string.Empty, string.Empty, string.Empty, string.Empty, Values: new Dictionary<string, string> { [StateFields.Rg] = "genA" });

    private static async Task<(MarkerSweeper Sweeper, StateRecord State, string L)> StartAsync(StateKit kit)
    {
        var record = kit.Names.Control(kit.Group, 0);
        await kit.Scripts.ControlSyncAsync(record, new ControlRecord(1, Id, "S3", "fwd", "1", "S0", "none", "0", "0", "id0", [("c1", "g1")]) { Limit = 100 });

        // A marker comes with the peak field, which a writer's script keeps.
        await kit.ControlDb.HashSetAsync(record, "pk", "1");
        var l = "sweep-l-" + Guid.NewGuid().ToString("N");
        await kit.ControlDb.StringSetAsync(l, "older");
        var state = new StateRecord("S3", "fwd", 1, Id, true, 1, string.Empty, null, "c1", "cs", string.Empty, string.Empty, string.Empty, string.Empty);
        return (new MarkerSweeper(kit.ControlDb, kit.Scripts, kit.Names, kit.Options, kit.Time), state, l);
    }
}
