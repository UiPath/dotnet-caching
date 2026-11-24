using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// On a cluster, every script and every staging key of a hash form sits in the one slot of its data key, so the forms run where L and N live in different slots.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashClusterTests(RedisFixture fixture)
{
    private static readonly Dictionary<string, string> Original = new() { ["a"] = "1", ["b"] = "2", ["c"] = "3" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_promotion_a_merge_a_replace_and_a_field_delete_run_across_the_slots_of_L_and_N()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, cluster: true);
        await kit.Kit.PrepareAsync("k", "m", "r", "f");
        await kit.Kit.SetStateAsync("S3");
        foreach (var key in new[] { "k", "m", "r", "f" })
        {
            await kit.SetAsync(kit.L(key), Original, TimeSpan.FromMinutes(5));
        }

        (await kit.Client.ReadAsync("k", null, Ct)).Fields!.Count.Should().Be(3);
        (await kit.Client.SetAsync("m", KeyMoveHashKit.Items(("d", "4")), new HashWriteOptions(HashCacheSetOption.HashReplace, null), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.SetAsync("r", KeyMoveHashKit.Items(("x", "9")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.RemoveFieldsAsync("f", ["a"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
        (await kit.FieldsAsync(kit.N("m"))).Should().BeEquivalentTo(new Dictionary<string, string>(Original) { ["d"] = "4" });
        (await kit.FieldsAsync(kit.N("r"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["x"] = "9" });
        (await kit.FieldsAsync(kit.L("f"))).Keys.Should().BeEquivalentTo("b", "c");
        foreach (var key in new[] { "k", "m", "r" })
        {
            (await kit.Kit.ExistsAsync(kit.L(key))).Should().BeFalse(key);
        }
    }

    [Fact]
    public async Task A_fill_stages_in_the_slot_of_its_destination_in_S1_and_in_S3_and_a_refresh_and_a_NoOrigin_replace_work()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, cluster: true);
        await kit.Kit.PrepareAsync("l", "n");
        await kit.Kit.SetStateAsync("S1", nPop: 0);
        (await kit.Client.FillAsync("l", KeyMoveHashKit.Items(("a", "1")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.L("l"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "1" });

        await kit.Kit.SetStateAsync("S3");
        (await kit.Client.FillAsync("n", KeyMoveHashKit.Items(("a", "1")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.RefreshAsync("n", CacheExpiry.After(TimeSpan.FromHours(1)), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.N("n"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "1" });
        (await kit.Db.KeyTimeToLiveAsync(kit.N("n")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(50));
    }

    [Fact]
    public async Task A_NoOrigin_replace_writes_N_with_its_marker_in_the_slot_of_N_and_then_deletes_L()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, cluster: true, noOrigin: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["x"] = "9" });
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        await kit.Client.Unmarks.RunDueAsync(all: true);
        (await kit.Kit.MarkerAsync("k")).Should().BeNull();
    }
}
