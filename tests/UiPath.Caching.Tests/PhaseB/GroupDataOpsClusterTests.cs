using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

[Trait("Category", "Integration")]
public class GroupDataOpsClusterTests
{
    [Theory]
    [InlineData("app:")]
    [InlineData("{shard}:")]
    public async Task A_purge_scans_every_master_under_the_connector_prefix_and_deletes_each_key_through_the_record_of_its_slot(string prefix)
    {
        await using var kit = await DataOpsKit.ClusterAsync(prefix);
        var keys = new List<string>();
        for (var n = 0; n < 60; n++)
        {
            keys.Add(await kit.SetAsync(MoveSide.Native, n % 2 == 0 ? "a" : "b", "k" + n));
        }

        var unmanaged = await kit.SetAsync(MoveSide.Native, "c", "k");
        var unprefixed = kit.Space.PrefixOf(MoveSide.Native) + "a:bare";
        await kit.Kit.Db.StringSetAsync(unprefixed, "v");
        var state = await kit.StateAsync();

        (await kit.Data.CountNativeAsync(kit.Group, CancellationToken.None)).Should().Be(60, "the pattern starts with the prefix and the prefix is stripped before the ownership predicate");
        await kit.Data.PurgeNativeAsync(kit.Group, new DataGuard(state.Id, null), CancellationToken.None);

        foreach (var key in keys)
        {
            (await kit.ExistsAsync(key)).Should().BeFalse(key);
        }

        (await kit.ExistsAsync(unmanaged)).Should().BeTrue();
        (await kit.Kit.Db.KeyExistsAsync(unprefixed)).Should().BeTrue("a key outside the connector prefix is not the group's");
        await kit.Kit.Db.KeyDeleteAsync(unprefixed);
    }

    [Fact]
    public async Task A_purge_under_a_moved_state_is_refused_on_the_cluster()
    {
        await using var kit = await DataOpsKit.ClusterAsync("app:");
        var key = await kit.SetAsync(MoveSide.Native, "a", "k1");
        var old = await kit.StateAsync();
        await kit.MoveToAsync("S2");

        var act = () => kit.Data.PurgeNativeAsync(kit.Group, new DataGuard(old.Id, null), CancellationToken.None);

        await act.Should().ThrowAsync<PurgeRefusedException>();
        (await kit.ExistsAsync(key)).Should().BeTrue();
    }
}
