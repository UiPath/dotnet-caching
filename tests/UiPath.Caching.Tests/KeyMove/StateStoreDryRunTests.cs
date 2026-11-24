using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StateStoreDryRunTests(RedisContainerFixture fixture)
{
    private static readonly DryRunStamp Stamp = new(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), 4096, "movable=3");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_dry_run_stores_the_stamp_and_replaces_the_report_with_a_life(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var state = await kit.InitAsync("a");
        var report = kit.Names.DryRun(kit.Group);
        await kit.Db.HashSetAsync(report, "stale", "1");

        var stored = await kit.Store.StoreDryRunAsync(kit.Group, "fwd", state.Id, Stamp, Fields(("units", "2"), ("scanned", "9")), TimeSpan.FromMinutes(5));

        stored.Should().BeTrue();
        (await kit.Db.HashGetAsync(kit.StateKey, StateFields.DryRun)).ToString().Should().Be(Stamp.Encode());
        (await kit.Db.HashGetAllAsync(report)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString()).Should().BeEquivalentTo(new Dictionary<string, string> { ["units"] = "2", ["scanned"] = "9" });
        (await kit.Db.KeyTimeToLiveAsync(report)).Should().BeGreaterThan(TimeSpan.FromMinutes(4)).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        await kit.Db.KeyDeleteAsync(report);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_report_without_a_stamp_leaves_the_stamp_and_uses_the_reverse_key(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var state = await kit.InitAsync("a");
        await kit.Store.StoreDryRunAsync(kit.Group, "fwd", state.Id, Stamp, Fields(("units", "1")), TimeSpan.FromMinutes(5));

        var stored = await kit.Store.StoreDryRunAsync(kit.Group, "rev", state.Id, null, Fields(("units", "7")), TimeSpan.FromMinutes(5));

        stored.Should().BeTrue();
        (await kit.Db.HashGetAsync(kit.StateKey, StateFields.DryRun)).ToString().Should().Be(Stamp.Encode());
        (await kit.Db.HashGetAsync(kit.Names.DryRun(kit.Group, "rev"), "units")).ToString().Should().Be("7");
        (await kit.Db.HashGetAsync(kit.Names.DryRun(kit.Group), "units")).ToString().Should().Be("1");
        await kit.Db.KeyDeleteAsync([kit.Names.DryRun(kit.Group), kit.Names.DryRun(kit.Group, "rev")]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Without_a_state_hash_nothing_is_written(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);

        var stored = await kit.Store.StoreDryRunAsync(kit.Group, "fwd", MoveScripts.NewNonce(), Stamp, Fields(("units", "2")), TimeSpan.FromMinutes(5));

        stored.Should().BeFalse();
        (await kit.Db.KeyExistsAsync(kit.StateKey)).Should().BeFalse();
        (await kit.Db.KeyExistsAsync(kit.Names.DryRun(kit.Group))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_changed_id_leaves_the_previous_stamp_and_report_untouched(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var state = await kit.InitAsync("a");
        var report = kit.Names.DryRun(kit.Group);
        await kit.Db.HashSetAsync(report, "old", "1");
        await kit.Db.HashSetAsync(kit.StateKey, StateFields.DryRun, Stamp.Encode());
        await kit.Db.HashSetAsync(kit.StateKey, StateFields.Id, MoveScripts.NewNonce());
        try
        {
            var stored = await kit.Store.StoreDryRunAsync(kit.Group, "fwd", state.Id, Stamp with { EstimatedBytes = 1 }, Fields(("units", "2")), TimeSpan.FromMinutes(5));

            stored.Should().BeFalse();
            (await kit.Db.HashGetAsync(kit.StateKey, StateFields.DryRun)).ToString().Should().Be(Stamp.Encode());
            (await kit.Db.HashGetAllAsync(report)).Should().Equal(new HashEntry("old", "1"));
        }
        finally
        {
            await kit.Db.KeyDeleteAsync(report);
        }
    }

    [Fact]
    public void A_dry_run_key_rejects_an_unknown_direction()
    {
        var names = new MoveKeyNames(string.Empty, "lib:", cluster: false);

        var format = () => names.DryRun("group", "sideways");

        format.Should().Throw<ArgumentException>().WithParameterName("direction");
    }

    private static KeyValuePair<string, string>[] Fields(params (string Name, string Value)[] fields) => [.. fields.Select(f => KeyValuePair.Create(f.Name, f.Value))];
}
