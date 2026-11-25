using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// A unit admitted at e-1 is refused by the server after EPSYNC and re-run under e; the node acks e only when older units are done.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StageAdmissionTests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_unit_admitted_at_e_minus_1_gets_minus_1_after_EPSYNC_is_re_run_under_e_and_the_node_acks_only_then(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var s0 = await kit.InitAsync("a");
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(s0), StateKit.TargetFrom(s0, "S1", "fwd", 0), null, CancellationToken.None);
        var monitor = new GroupStateMonitor(kit.Store, new KeyMoveStateOptions(), kit.Time);
        monitor.Register([new GroupRegistration(kit.Group, false, true)]);
        var gate = new EpochGate();
        var acks = new AckCoordinator(monitor, gate, new StageDriverOptions(), kit.Time);
        await monitor.StepAsync(kit.Group, CancellationToken.None);
        acks.Tick(kit.Group).Should().BeTrue();
        var e1 = monitor.RulesFor(kit.Group).Acting!;
        var keyText = "t9:{" + kit.Group + "}:k";
        var key = (RedisKey)keyText;
        var control = kit.Names.ControlFor(kit.Group, keyText);
        await using var cleanup = new KeyCleanup(kit.Db, key);

        using var unit = gate.Enter(kit.Group)!;
        (await kit.Scripts.SourceSetAsync(key, control, unit.Id, "v1", MoveScripts.NoExpiry, onlyIfAbsent: false)).Should().Be(MoveScripts.Written);

        // A new identity is committed while the unit is still in flight.
        var s1 = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(s1), StateKit.TargetFrom(s1, "S3", "fwd", 1), null, CancellationToken.None);
        await monitor.StepAsync(kit.Group, CancellationToken.None);
        acks.Tick(kit.Group).Should().BeFalse("a unit of the older identity is still counted");
        monitor.AckedFor(kit.Group).Should().BeNull();
        var e3 = monitor.RulesFor(kit.Group).Acting!;
        e3.Ep.Should().BeGreaterThan(e1.Ep);

        var refused = await kit.Scripts.SourceSetAsync(key, control, unit.Id, "v2", MoveScripts.NoExpiry, onlyIfAbsent: false);
        refused.Should().Be(MoveScripts.IdentityMismatch, "the server revokes the delayed script of the old identity");
        (await kit.Db.StringGetAsync(key)).Should().Be("v1");

        unit.Readmit().Should().BeTrue();
        (await kit.Scripts.SourceSetAsync(key, control, unit.Id, "v2", MoveScripts.NoExpiry, onlyIfAbsent: false)).Should().Be(MoveScripts.Written);
        acks.Tick(kit.Group).Should().BeTrue();
        monitor.AckedFor(kit.Group).Should().Be(new AckedIdentity(e3.Ep, e3.Id, "S3", "fwd"));
    }

    [Fact]
    public async Task A_stale_unit_that_never_completes_keeps_the_node_from_acknowledging_so_the_transition_waits()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var s0 = await kit.InitAsync("a");
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(s0), StateKit.TargetFrom(s0, "S1", "fwd", 0), null, CancellationToken.None);
        var monitor = new GroupStateMonitor(kit.Store, new KeyMoveStateOptions(), kit.Time);
        monitor.Register([new GroupRegistration(kit.Group, false, true)]);
        var gate = new EpochGate();
        var acks = new AckCoordinator(monitor, gate, new StageDriverOptions(), kit.Time);
        await monitor.StepAsync(kit.Group, CancellationToken.None);
        acks.Tick(kit.Group);
        using var stuck = gate.Enter(kit.Group)!;
        var s1 = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(s1), StateKit.TargetFrom(s1, "S3", "fwd", 1), null, CancellationToken.None);
        await monitor.StepAsync(kit.Group, CancellationToken.None);

        for (var n = 0; n < 5; n++)
        {
            acks.Tick(kit.Group).Should().BeFalse();
        }

        gate.OlderUnits(kit.Group).Should().Be(1);
        monitor.AckedFor(kit.Group).Should().BeNull("the leader waits and alerts; the node advertises no identity until it drains");
    }

    private sealed class KeyCleanup(IDatabase db, RedisKey key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await db.KeyDeleteAsync(key);
    }
}
