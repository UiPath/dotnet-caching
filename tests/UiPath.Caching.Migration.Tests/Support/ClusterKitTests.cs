using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.Support;

// The faults of the kit do what the fault tests rely on. They share the collection of the other fault tests, so a failover never runs under one of them.
[Collection("RedisIntegration")]
[Trait("Category", "Chaos")]
public sealed class ClusterKitTests(RedisFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failover_moves_the_slot_to_the_former_replica_and_a_replicated_value_stays(bool takeover)
    {
        await using var kit = await ClusterKit.CreateAsync();
        var key = (RedisKey)("kit:failover:" + Guid.NewGuid().ToString("N"));
        var db = kit.Mux.GetDatabase();
        var before = kit.MasterOf(key);
        var formerReplica = kit.ReplicaOf(before);
        await db.StringSetAsync(key, "v");
        ((long)await before.ExecuteAsync("WAIT", "1", "5000")).Should().BeGreaterThanOrEqualTo(1);

        var after = await kit.FailoverAsync(key, takeover);

        after.EndPoint.Should().Be(formerReplica.EndPoint);
        kit.MasterOf(key).EndPoint.Should().Be(after.EndPoint);
        ((string?)await db.StringGetAsync(key)).Should().Be("v");
    }

    [Fact]
    public async Task A_restarted_replica_has_a_new_run_id_and_replication_is_ready()
    {
        await using var kit = await ClusterKit.CreateAsync();
        var key = (RedisKey)("kit:restart:" + Guid.NewGuid().ToString("N"));
        var db = kit.Mux.GetDatabase();
        var master = kit.MasterOf(key);
        var replica = kit.ReplicaOf(master);
        var before = await ClusterKit.ReadRunIdAsync(replica);
        var oldInfo = replica.InfoAsync("server");
        await oldInfo;
        var reads = 0;
        var observedReplica = InterceptingProxy.Wrap(replica, (method, _) =>
            method.Name == nameof(IServer.InfoAsync) && ++reads <= 2 ? oldInfo : InterceptingProxy.Proceed);
        await db.StringSetAsync(key, "before");
        ((long)await master.ExecuteAsync("WAIT", "1", "5000")).Should().Be(1);

        await kit.RestartAsync(observedReplica);

        reads.Should().BeGreaterThanOrEqualTo(3);
        (await ClusterKit.ReadRunIdAsync(replica)).Should().NotBe(before);
        await db.StringSetAsync(key, "after");
        ((long)await master.ExecuteAsync("WAIT", "1", "1000")).Should().Be(1);
        kit.ReplicaOf(kit.MasterOf(key)).EndPoint.Should().Be(replica.EndPoint);
    }

    [Fact]
    public async Task Settlement_waits_for_the_replica_to_resume_replication()
    {
        await using var kit = await ClusterKit.CreateAsync();
        var key = (RedisKey)("kit:replication:" + Guid.NewGuid().ToString("N"));
        var master = kit.MasterOf(key);
        var replica = kit.ReplicaOf(master);
        var password = (await replica.ConfigGetAsync("masterauth")).Single().Value;
        var settling = Task.CompletedTask;
        await replica.ConfigSetAsync("masterauth", "invalid-password");
        try
        {
            ((long)await replica.ExecuteAsync("CLIENT", "KILL", "TYPE", "MASTER")).Should().Be(1);
            await ClusterKit.UntilAsync(async () =>
                ((string?)await replica.ExecuteAsync("INFO", "replication"))?.Contains("master_link_status:down", StringComparison.Ordinal) == true);

            settling = kit.WaitSettledAsync();
            await Assert.ThrowsAsync<TimeoutException>(() => settling.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        }
        finally
        {
            await replica.ConfigSetAsync("masterauth", password);
            await settling;
        }

        await kit.Mux.GetDatabase().StringSetAsync(key, "replicated");
        ((long)await master.ExecuteAsync("WAIT", "1", "1000")).Should().Be(1);
    }

    [Fact]
    public async Task Restart_propagates_a_rejected_shutdown()
    {
        await using var kit = await ClusterKit.CreateAsync();
        var replica = kit.ReplicaOf(kit.MasterOf("kit:restart:rejected"));
        var rejection = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "Shutdown rejected.");
        var server = InterceptingProxy.Wrap(replica, (method, args) =>
            method.Name == nameof(IServer.ExecuteAsync) && Equals(args[0], "SHUTDOWN")
                ? Task.FromException<RedisResult>(rejection)
                : InterceptingProxy.Proceed);

        var failure = await Assert.ThrowsAsync<RedisServerException>(() => kit.RestartAsync(server));

        failure.Should().BeSameAs(rejection);
    }

    [Fact]
    public async Task Polling_retries_redis_command_timeouts()
    {
        var attempts = 0;

        await ClusterKit.UntilAsync(() => ++attempts <= 2
            ? Task.FromException<bool>(new RedisTimeoutException(CommandFlags.None, "Command timed out.", CommandStatus.Sent))
            : Task.FromResult(true));

        attempts.Should().Be(3);
    }

    [Fact]
    public async Task A_settle_timeout_disposes_the_cluster_connection()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        mux.IsConnected.Should().BeTrue();
        var timeout = new TimeoutException("The cluster did not settle in time.");

        var failure = await Assert.ThrowsAsync<TimeoutException>(() => ClusterKit.CreateAsync(mux, _ => Task.FromException(timeout)));

        failure.Should().BeSameAs(timeout);
        mux.IsConnected.Should().BeFalse();
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
    }
}
