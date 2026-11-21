using System.Globalization;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class MasterCensusTests(RedisContainerFixture fixture)
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task The_census_counts_owned_source_keys_per_cache_with_a_zero_for_a_cache_that_holds_none()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        await kit.SetAsync(MoveSide.Legacy, "a", "k1");
        await kit.SetAsync(MoveSide.Legacy, "a", "mv2");
        await kit.SetAsync(MoveSide.Legacy, "c", "k3");
        await kit.SetAsync(MoveSide.Native, "b", "k4");

        var census = (await kit.Census.RunAsync(kit.Group, Ct))!;

        census.Residue.Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 2, ["b"] = 0 });
        census.Movable.Should().Be(1);
    }

    [Fact]
    public async Task A_reverse_census_counts_the_native_side()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        await kit.SetAsync(MoveSide.Native, "b", "k1");
        await kit.SetAsync(MoveSide.Legacy, "b", "k2");
        await kit.MoveToAsync("S4", "rev", 1);

        var census = (await kit.Census.RunAsync(kit.Group, Ct))!;

        census.Residue["b"].Should().Be(1);
        MasterCensus.SourceOf(await kit.StateAsync()).Should().Be(MoveSide.Native);
    }

    [Fact]
    public async Task The_canary_counts_keys_whose_life_says_they_were_written_after_S3_and_needs_the_time_of_S3()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        kit.Kit.Time.Advance(TimeSpan.FromDays(1));
        kit.Space.MaxTtl["a"] = TimeSpan.FromMinutes(10);
        await kit.SetAsync(MoveSide.Legacy, "a", "long", TimeSpan.FromHours(1));
        await kit.SetAsync(MoveSide.Legacy, "a", "short", TimeSpan.FromSeconds(30));
        await kit.SetAsync(MoveSide.Legacy, "a", "never");
        (await kit.Census.RunAsync(kit.Group, Ct))!.Canary.Should().BeNull("no S3 time, no evidence");

        var state = await kit.StateAsync();
        var t3 = kit.Kit.Time.GetUtcNow().AddMinutes(-2).ToUnixTimeMilliseconds();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, t3.ToString(CultureInfo.InvariantCulture))], []);

        (await kit.Census.RunAsync(kit.Group, Ct))!.Canary.Should().Be(1, "an hour of life and two minutes since S3 beats the longest source life; thirty seconds does not; no expiry is not seen");
    }

    [Fact]
    public async Task The_drain_report_never_passes_without_a_mover_pass_and_then_needs_nothing_movable()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var state = await kit.StateAsync();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, "1")], []);
        await kit.SetAsync(MoveSide.Legacy, "a", "k1");

        (await new CensusMoverStatus(kit.Census).DrainAsync(kit.Group, "fwd", Ct))!.Passed.Should().BeFalse("only the mover can say its pass finished");
        (await new CensusMoverStatus(kit.Census, (_, _, _) => Task.FromResult(true)).DrainAsync(kit.Group, "fwd", Ct))!.Should().Match<DrainReport>(r => r.Passed && r.Residue["a"] == 1 && r.Canary == 0);
        await kit.SetAsync(MoveSide.Legacy, "a", "mv2");
        (await new CensusMoverStatus(kit.Census, (_, _, _) => Task.FromResult(true)).DrainAsync(kit.Group, "fwd", Ct))!.Passed.Should().BeFalse("a movable key is left");
    }

    [Fact]
    public async Task A_forward_report_is_null_without_the_time_of_S3_and_the_residue_census_still_answers()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        await kit.SetAsync(MoveSide.Legacy, "b", "k1");
        var status = new CensusMoverStatus(kit.Census);

        (await status.DrainAsync(kit.Group, "fwd", Ct)).Should().BeNull();
        (await status.ResidueCensusAsync(kit.Group, Ct))["b"].Should().Be(1);
        (await status.ResidueCensusAsync("g" + Guid.NewGuid().ToString("N")[..8], Ct)).Should().BeEmpty();
    }
}
