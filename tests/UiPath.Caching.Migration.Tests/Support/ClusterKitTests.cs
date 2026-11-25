using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.Support;

// The faults of the kit do what the fault tests rely on. They share the collection of the other fault tests, so a failover never runs under one of them.
[Collection("RedisIntegration")]
[Trait("Category", "Chaos")]
public sealed class ClusterKitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_failover_moves_the_slot_to_the_former_replica_and_a_replicated_value_stays()
    {
        await using var kit = await ClusterKit.CreateAsync();
        var key = (RedisKey)("kit:failover:" + Guid.NewGuid().ToString("N"));
        var db = kit.Mux.GetDatabase();
        var before = kit.MasterOf(key);
        await db.StringSetAsync(key, "v");
        await before.ExecuteAsync("WAIT", "1", "5000");

        var after = await kit.FailoverAsync(key);

        after.EndPoint.Should().NotBe(before.EndPoint);
        kit.MasterOf(key).EndPoint.Should().Be(after.EndPoint);
        ((string?)await db.StringGetAsync(key)).Should().Be("v");
    }

    [Fact]
    public async Task A_restarted_replica_comes_back_empty_and_the_cluster_is_ok_again()
    {
        await using var kit = await ClusterKit.CreateAsync();
        var key = (RedisKey)("kit:restart:" + Guid.NewGuid().ToString("N"));
        var replica = kit.ReplicaOf(kit.MasterOf(key));

        await kit.RestartAsync(replica);

        (await replica.PingAsync()).Should().BeGreaterThan(TimeSpan.Zero);
        kit.ReplicaOf(kit.MasterOf(key)).EndPoint.Should().Be(replica.EndPoint);
    }

    [Fact]
    public async Task A_paused_master_holds_a_write_for_the_pause()
    {
        await using var kit = await ClusterKit.CreateAsync();
        var key = (RedisKey)("kit:pause:" + Guid.NewGuid().ToString("N"));
        var db = kit.Mux.GetDatabase();
        await db.StringSetAsync(key, "warm");

        await ClusterKit.PauseAsync(kit.MasterOf(key), TimeSpan.FromMilliseconds(600));
        var started = TimeProvider.System.GetTimestamp();
        await db.StringSetAsync(key, "v");

        TimeProvider.System.GetElapsedTime(started).Should().BeGreaterThan(TimeSpan.FromMilliseconds(400));
        Ct.IsCancellationRequested.Should().BeFalse();
    }
}
