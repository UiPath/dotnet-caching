namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class RedisContainerFixtureTests(RedisContainerFixture fixture)
{
    // A skip on Redis or Valkey would hide a regression, so no probe may be false there.
    [Fact]
    public void Every_capability_flag_is_true_on_a_Redis_or_Valkey_server()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipWhen(fixture.IsCluster, "A cluster keeps ACL users, scripts and subscriptions per node, so the probes do not all answer as one server's.");
        Assert.SkipUnless(fixture.CountsSubscribersPerClient, "PUBSUB NUMSUB of this server is not a client count (a proxy such as Azure Managed Redis).");
        Assert.SkipUnless(fixture.IsReferenceServer, "Only Redis and Valkey 7 and later must pass every probe; this server is " + (fixture.ServerKind.Length == 0 ? $"version {fixture.ServerVersion}" : fixture.ServerKind) + ".");

        fixture.SupportsStreams.Should().BeTrue();
        fixture.StreamsKeepGroupsAfterDelete.Should().BeFalse();
        fixture.ReportsEntriesRead.Should().BeTrue();
        fixture.SupportsShardedPubSub.Should().BeTrue();
        fixture.SupportsEvalRo.Should().BeTrue();
        fixture.ReportsCommandStats.Should().BeTrue();
        fixture.SupportsClientKillByType.Should().BeTrue();
        fixture.ReportsAclLog.Should().BeTrue();
    }
}
