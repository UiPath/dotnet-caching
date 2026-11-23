using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class LeaderLeaseTests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task One_node_holds_the_lease_at_a_time_and_the_key_carries_the_library_prefix(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var key = kit.Names.Lead(kit.Group);
        var first = new LeaderLease(kit.Scripts, key, kit.Options, kit.Time);
        var second = new LeaderLease(kit.Scripts, key, kit.Options, kit.Time);

        (await first.TryHoldAsync()).Should().BeTrue();
        (await second.TryHoldAsync()).Should().BeFalse();

        key.ToString().Should().Be($"{StateKit.Library}mg:{{{kit.Group}}}:lead");
        first.HoldsLease.Should().BeTrue();
        second.HoldsLease.Should().BeFalse();
        await first.ReleaseAsync();
        first.HoldsLease.Should().BeFalse();
        (await second.TryHoldAsync()).Should().BeTrue();
        await second.ReleaseAsync();
    }

    [Fact]
    public async Task A_holder_that_cannot_renew_stops_writing_when_the_time_it_could_rely_on_runs_out()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var lease = new LeaderLease(kit.Scripts, kit.Names.Lead(kit.Group), kit.Options, kit.Time);
        await lease.TryHoldAsync();

        kit.Time.Advance(TimeSpan.FromSeconds(29));
        lease.HoldsLease.Should().BeTrue();
        await lease.TryHoldAsync();
        kit.Time.Advance(TimeSpan.FromSeconds(29));
        lease.HoldsLease.Should().BeTrue("the renewal bought another lifetime");

        kit.Time.Advance(TimeSpan.FromSeconds(2));

        lease.HoldsLease.Should().BeFalse();
        await lease.ReleaseAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_holder_whose_lease_expired_on_the_server_learns_it_when_another_node_has_taken_it(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, new KeyMoveStateOptions { ExpectedReplicasPerMaster = 0, LeaseTtl = TimeSpan.FromMilliseconds(300) });
        var key = kit.Names.Lead(kit.Group);
        var stalled = new LeaderLease(kit.Scripts, key, kit.Options, kit.Time);
        var other = new LeaderLease(kit.Scripts, key, kit.Options, kit.Time);
        await stalled.TryHoldAsync();

        await Task.Delay(450, TestContext.Current.CancellationToken);
        (await other.TryHoldAsync()).Should().BeTrue();

        (await stalled.TryHoldAsync()).Should().BeFalse();
        stalled.HoldsLease.Should().BeFalse();
        await other.ReleaseAsync();
    }

    [Fact]
    public async Task The_loop_takes_the_lease_renews_it_every_ten_seconds_and_releases_it_when_stopped()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var key = kit.Names.Lead(kit.Group);
        var lease = new LeaderLease(kit.Scripts, key, kit.Options, kit.Time);
        using var stop = new CancellationTokenSource();
        var loop = lease.RunAsync(stop.Token);
        await TimeSteps.UntilAsync(() => lease.HoldsLease);

        // A renewal is seen by the key coming back after it was deleted behind the loop's back.
        await kit.Db.KeyDeleteAsync(key);
        await TimeSteps.AdvanceArmedAsync(kit.Time, TimeSpan.FromSeconds(9));
        (await kit.Db.KeyExistsAsync(key)).Should().BeFalse("ten seconds have not passed");
        await TimeSteps.AdvanceArmedAsync(kit.Time, TimeSpan.FromSeconds(1));
        await TimeSteps.UntilAsync(() => kit.Db.KeyExists(key));

        await stop.CancelAsync();
        await loop;
        (await kit.Db.KeyExistsAsync(key)).Should().BeFalse();
        lease.HoldsLease.Should().BeFalse();
    }
}
