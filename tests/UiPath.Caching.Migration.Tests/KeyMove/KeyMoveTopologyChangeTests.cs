using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A slot that moves to another master while keys of the group move: the slot is split between two masters for a while, and every value read
// after the move is the newest one acknowledged.
[Collection("RedisIntegration")]
[Trait("Category", "Chaos")]
public sealed class KeyMoveTopologyChangeTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_promotion_and_a_write_while_the_slot_of_L_is_split_leave_the_newest_acknowledged_value(bool scripts)
    {
        await using var cluster = await ClusterKit.CreateAsync();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, cluster: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");

        // The control record of L's slot moves first, so a script that needs both it and L is refused until the slot has moved.
        var slot = kit.Mux.HashSlot(kit.L("k"));
        var migration = await cluster.BeginMigrationAsync(slot, kit.Names.ControlFor(kit.Group, kit.L("k")));

        var during = await kit.Client.ReadAsync("k", Ct);
        var written = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        await cluster.FinishMigrationAsync(migration);
        await kit.Mux.ConfigureAsync();
        await kit.Client.Retries.RunDueAsync(all: true);

        during.Value!.Name.Should().Be("v0", "the read during the move serves the value there is");
        var after = (await kit.Client.ReadAsync("k", Ct)).Value?.Name;
        if (written.Outcome == WriteOutcome.Committed)
        {
            after.Should().Be("v1", "an acknowledged write is never lost to the move");
        }
        else
        {
            after.Should().BeOneOf("v0", "v1");
        }

        // Once the slot has moved, every form runs again.
        (await kit.Client.SetAsync("k", new Item("v2"), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v2");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_promotion_after_the_slot_of_N_moved_publishes_under_the_fence_on_its_new_master()
    {
        await using var cluster = await ClusterKit.CreateAsync();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, cluster: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        var slot = kit.Mux.HashSlot(kit.N("k"));
        var before = ShardFailingDatabase.OwnerOf(kit.Mux, kit.N("k"));

        await cluster.FinishMigrationAsync(await cluster.BeginMigrationAsync(slot));
        await kit.Mux.ConfigureAsync();

        ShardFailingDatabase.OwnerOf(kit.Mux, kit.N("k")).Should().NotBe(before);
        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v0");
        (await kit.GetAsync(kit.N("k"))).Should().Be("v0");
        (await kit.FenceAsync("k")).Should().NotBeNull();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }
}
