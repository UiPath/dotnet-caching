using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

/// <summary>
/// Side capabilities, views, barriers and connection counters of the instance move. Beyond the shared test server: MOVE_SIDES_MASTER and MOVE_SIDES_REPLICA
/// (a master and its replica, the replica with the debug command enabled) and MOVE_SIDES_LIMITED (a user without SCAN, ROLE, CONFIG, INFO and WAIT).
/// </summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoveSidesTests(RedisContainerFixture fixture)
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.";
    private static readonly SideCaps Good = new(new Version(7, 4), Scripts: true, Wait: true, ScanMode.PerShard, SlotEnforced: false, "noeviction", 1, []);

    private static string? MasterWithReplica => Environment.GetEnvironmentVariable("MOVE_SIDES_MASTER");

    private static string? Replica => Environment.GetEnvironmentVariable("MOVE_SIDES_REPLICA");

    private static string? Limited => Environment.GetEnvironmentVariable("MOVE_SIDES_LIMITED");

    [Fact]
    public void S3_G6_is_the_conjunction_of_both_sides_and_names_the_failing_side()
    {
        SideCaps.Gate(Good, Good).Should().BeEmpty();
        SideCaps.Gate(Good, Good with { Wait = false, Denied = ["WAIT", "ROLE"] }).Should().BeEmpty("WAIT needs an approval, not the gate");
        SideCaps.Gate(Good, Good with { Scripts = false }).Should().BeEmpty("a side without scripts takes the fallback on 6.0.9");

        var failures = SideCaps.Gate(Good with { Scripts = false, Version = new Version(6, 0, 8) }, Good with { Scan = ScanMode.None, EvictionPolicy = "allkeys-lru" });

        failures.Should().HaveCount(3);
        failures.Should().Contain(f => f.StartsWith("src:", StringComparison.Ordinal) && f.Contains("fallback", StringComparison.Ordinal));
        failures.Should().Contain(f => f.StartsWith("dst:", StringComparison.Ordinal) && f.Contains("scan", StringComparison.Ordinal));
        failures.Should().Contain(f => f.StartsWith("dst:", StringComparison.Ordinal) && f.Contains("allkeys-lru", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("noeviction", true)]
    [InlineData("volatile-lru", true)]
    [InlineData("allkeys-lfu", false)]
    [InlineData(null, null)]
    public void Eviction_is_safe_only_for_policies_that_never_evict_a_key_without_a_ttl(string? policy, bool? safe) =>
        (Good with { EvictionPolicy = policy }).EvictionSafe.Should().Be(safe);

    [Fact]
    public async Task A_plain_server_probes_as_scripts_per_shard_scan_no_replica_and_no_wait()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
        var replicas = await mux.GetServer(mux.GetEndPoints()[0]).RoleAsync() is Role.Master m ? m.Replicas.Count : 0;
        Assert.SkipUnless(replicas == 0, "The shared test server has replicas.");

        var caps = await SideProbe.ProbeAsync(mux.GetDatabase(), "sidetest:mg:", "sidetest:*");

        caps.Scripts.Should().BeTrue();
        caps.Replicas.Should().Be(0);
        caps.Wait.Should().BeFalse("no replica answers, and the probe does not wait for one that does not exist");
        caps.Scan.Should().Be(ScanMode.PerShard);
        caps.SlotEnforced.Should().BeFalse();
        caps.EvictionPolicy.Should().Be("noeviction");
        caps.Denied.Should().BeEmpty();
        caps.Failures("dst").Should().BeEmpty();
    }

    [Fact]
    public async Task A_server_that_refuses_scripts_is_probed_as_fallback_and_slot_enforced()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var refusing = InterceptingProxy.Wrap(mux.GetDatabase(), (method, _) =>
            method.Name == nameof(IDatabase.ScriptEvaluateAsync) ? Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "ERR unknown command 'EVAL', with args beginning with: ")) : InterceptingProxy.Proceed);

        var caps = await SideProbe.ProbeAsync(refusing, "sidetest:mg:", "sidetest:*");

        caps.Scripts.Should().BeFalse();
        caps.SlotEnforced.Should().BeTrue("without scripts the probe assumes slots are enforced, which is always safe");
        caps.MoveScriptsOn(refusing).UsesScripts.Should().BeFalse("this side takes the fallback on its own");
    }

    [Fact]
    public async Task A_user_denied_the_probed_commands_reports_each_denial_as_a_capability()
    {
        Assert.SkipUnless(Limited is not null, "Set MOVE_SIDES_LIMITED to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(Limited!);

        var caps = await SideProbe.ProbeAsync(mux.GetDatabase(), "sidetest:mg:", "sidetest:*");

        caps.Scan.Should().Be(ScanMode.None);
        caps.Replicas.Should().BeNull();
        caps.EvictionPolicy.Should().BeNull();
        caps.Denied.Should().Contain(["SCAN", "ROLE"]);
        caps.Failures("dst").Should().ContainSingle().Which.Should().Contain("scan");
    }

    [Fact]
    public async Task A_master_with_a_replica_probes_wait_and_its_barrier_confirms()
    {
        Assert.SkipUnless(MasterWithReplica is not null, "Set MOVE_SIDES_MASTER to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(MasterWithReplica!);
        var db = mux.GetDatabase();

        var caps = await SideProbe.ProbeAsync(db, "sidetest:mg:", "sidetest:*");
        caps.Wait.Should().BeTrue();
        caps.Replicas.Should().Be(1);

        var barrier = new ServerBarrier(mux.GetServer(mux.GetEndPoints()[0]), TimeProvider.System);
        await db.StringSetAsync("sidetest:k", "v");
        (await barrier.WaitAsync(() => Task.CompletedTask, wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
    }

    [Fact]
    public async Task A_replica_that_cannot_acknowledge_leaves_the_barrier_unconfirmed_and_a_way_back_then_skips_it()
    {
        Assert.SkipUnless(MasterWithReplica is not null && Replica is not null, "Set MOVE_SIDES_MASTER and MOVE_SIDES_REPLICA to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{MasterWithReplica},syncTimeout=10000");
        await using var replica = await ConnectionMultiplexer.ConnectAsync($"{Replica},allowAdmin=true,syncTimeout=10000");
        var db = mux.GetDatabase();
        var reissued = 0;
        var barrier = new ServerBarrier(mux.GetServer(mux.GetEndPoints()[0]), TimeProvider.System) { Timeout = TimeSpan.FromMilliseconds(200), Retries = 2 };

        var sleeping = replica.GetServer(replica.GetEndPoints()[0]).ExecuteAsync("DEBUG", "SLEEP", "2");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await db.StringSetAsync("sidetest:k", "v");
        var outcome = await barrier.WaitAsync(
            async () =>
            {
                reissued++;
                await db.StringSetAsync("sidetest:k", "v");
            },
            wayBack: false);

        outcome.Should().Be(BarrierOutcome.NotConfirmed, "0 or a timeout is not a confirmation");
        reissued.Should().Be(2, "each retry re-issues the writes, with no extra backoff");
        (await barrier.WaitAsync(() => Task.CompletedTask, wayBack: true)).Should().Be(BarrierOutcome.SkippedAfterRecentFailure);
        await sleeping;
    }

    [Fact]
    public async Task A_master_without_a_replica_needs_no_barrier()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var replicas = await mux.GetServer(mux.GetEndPoints()[0]).RoleAsync() is Role.Master m ? m.Replicas.Count : 0;
        Assert.SkipUnless(replicas == 0, "The shared test server has replicas.");

        (await new ServerBarrier(mux.GetServer(mux.GetEndPoints()[0]), TimeProvider.System).WaitAsync(() => Task.CompletedTask, wayBack: false))
            .Should().Be(BarrierOutcome.NoReplica);
    }

    [Fact]
    public async Task The_views_strip_and_restore_the_source_prefix_and_scan_raw()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var src = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        await using var dst = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        using var views = MoveViews.Create(src.GetDatabase().WithKeyPrefix("{tag}:"), dst.GetDatabase(), "{tag}:");

        views.ToLogical("{tag}:app:s:k").Should().Be("app:s:k");
        views.ToLogical("other:app:s:k").Should().BeNull("a key outside the Src prefix is not this move's");
        views.ToRaw("app:s:k").Should().Be((RedisKey)"{tag}:app:s:k");
        views.SourceScanPattern("app:*").Should().Be("{tag}:app:*");
        views.SourceRaw.Should().NotBeSameAs(views.SourcePrefixed);
    }

    [Fact]
    public async Task A_connection_restore_on_either_server_moves_the_counter()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var src = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
        await using var dst = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
        using var changes = new ConnectionChanges(src, dst);
        var mark = changes.Current;
        changes.ChangedSince(mark).Should().BeFalse();

        // Kills the Dst's connections: it fails, reconnects, and the counter moves.
        var id = (long)await dst.GetServer(dst.GetEndPoints()[0]).ExecuteAsync("CLIENT", "ID");
        await src.GetServer(src.GetEndPoints()[0]).ExecuteAsync("CLIENT", "KILL", "ID", id);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!changes.ChangedSince(mark) && DateTime.UtcNow < until)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        changes.ChangedSince(mark).Should().BeTrue();
    }
}
