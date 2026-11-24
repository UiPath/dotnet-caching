using StackExchange.Redis;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The seam the mover calls on a live marker: a guarded delete of L, then the removal of the exact marker.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveSupersessionHelperTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_helped_key_has_L_deleted_and_its_exact_marker_removed_and_is_retried(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.LeaveSupersededAsync("k", "v0", "n7");

        var action = await kit.Client.Supersession.OnSupersededAsync(new SupersededKey(kit.Group, kit.Cid, "k", kit.Id + "|n7", kit.Id), Ct);

        action.Should().Be(SupersessionAction.Retry);
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.MarkerAsync("k")).Should().BeNull();
    }

    [Fact]
    public async Task A_marker_that_changed_since_the_capture_is_left_but_L_is_still_deleted()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.LeaveSupersededAsync("k", "v0", "n8");

        var action = await kit.Client.Supersession.OnSupersededAsync(new SupersededKey(kit.Group, kit.Cid, "k", kit.Id + "|n7", kit.Id), Ct);

        action.Should().Be(SupersessionAction.Retry);
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.MarkerAsync("k")).Should().Be(kit.Id + "|n8", "a newer marker is not the helper's to remove");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stale_identity_is_a_rerun_and_nothing_is_changed(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        var stale = kit.Id;
        await kit.LeaveSupersededAsync("k", "v0");
        await kit.MoveGateAsync();

        var action = await kit.Client.Supersession.OnSupersededAsync(new SupersededKey(kit.Group, kit.Cid, "k", stale + "|n0", stale), Ct);

        action.Should().Be(SupersessionAction.Rerun);
        (await kit.GetAsync(kit.L("k"))).Should().Be("v0");
        (await kit.MarkerAsync("k")).Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_marker_whose_binding_ended_is_a_rerun_and_deletes_nothing(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "legitimate");
        await kit.Db.HashSetAsync(kit.Control("k"), kit.Field("k"), "id-of-an-older-run|n0");

        var action = await kit.Client.Supersession.OnSupersededAsync(new SupersededKey(kit.Group, kit.Cid, "k", "id-of-an-older-run|n0", kit.Id), Ct);

        action.Should().Be(SupersessionAction.Rerun);
        (await kit.GetAsync(kit.L("k"))).Should().Be("legitimate");
        (await kit.MarkerAsync("k")).Should().NotBeNull();
    }

    [Fact]
    public async Task Without_a_data_connection_the_key_is_skipped()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.LeaveSupersededAsync("k", "v0");
        kit.Plane.Data = null;

        var action = await kit.Client.Supersession.OnSupersededAsync(new SupersededKey(kit.Group, kit.Cid, "k", kit.Id + "|n0", kit.Id), Ct);

        action.Should().Be(SupersessionAction.Skip);
    }
}
