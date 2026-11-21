using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StateScriptsHarmlessWriteTests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_repeat_of_transit_durable_and_allack_still_writes_so_a_wait_has_an_offset(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        await using var probe = await WriteProbe.CreateAsync(fixture.ConnectionString);
        var state = env.Make("state");
        var target = new StateTarget("S1", "fwd", 0, 6, "id-6", "S0:none:0:5:id-5", 1);
        await env.Scripts.TransitAsync(state, StateExpectation.Absent, target);

        await probe.WatchAsync(state);
        (await env.Scripts.TransitAsync(state, StateExpectation.Absent, target)).Should().Be(MoveScripts.Repeated);
        (await probe.TouchedAsync()).Should().BeTrue("a TRANSIT repeat rewrites the id");

        (await env.Scripts.DurableAsync(state, "id-6")).Should().Be(1);
        await probe.WatchAsync(state);
        (await env.Scripts.DurableAsync(state, "id-6")).Should().Be(1);
        (await probe.TouchedAsync()).Should().BeTrue("a DURABLE repeat writes dur and prev again");

        (await env.Scripts.AllAckAsync(state, "id-6")).Should().Be(1);
        await probe.WatchAsync(state);
        (await env.Scripts.AllAckAsync(state, "id-6")).Should().Be(1);
        (await probe.TouchedAsync()).Should().BeTrue("an ALLACK repeat writes the same value again");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_branch_that_fails_its_predicate_writes_nothing(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        await using var probe = await WriteProbe.CreateAsync(fixture.ConnectionString);
        var state = env.Make("state");
        await env.Scripts.TransitAsync(state, StateExpectation.Absent, new StateTarget("S1", "fwd", 0, 6, "id-6", "S0:none:0:5:id-5", 1));
        await probe.WatchAsync(state);

        (await env.Scripts.DurableAsync(state, "other")).Should().Be(0);
        (await env.Scripts.AllAckAsync(state, "other")).Should().Be(0);
        (await env.Scripts.TransitAsync(state, StateExpectation.Holding(5, "id-5", 1), new StateTarget("S2", "fwd", 0, 9, "id-9", "p", 1))).Should().Be(0);

        (await probe.TouchedAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_probe_sees_a_real_write(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        await using var probe = await WriteProbe.CreateAsync(fixture.ConnectionString);
        var state = env.Make("state");
        await env.Scripts.TransitAsync(state, StateExpectation.Absent, new StateTarget("S1", "fwd", 0, 6, "id-6", "p", 1));
        await probe.WatchAsync(state);

        await env.Scripts.AllAckAsync(state, "id-6");

        (await probe.TouchedAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_run_that_a_script_refused_issues_no_barrier(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, new PhaseBStateOptions { ExpectedReplicasPerMaster = 1 });
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 1;

        var result = await kit.Sequence.RunAsync(kit.Group, StateExpectation.Holding(init.Ep, "other", 1), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.StateChanged);
        kit.Topology.Waits.Should().Be(0);
    }
}
