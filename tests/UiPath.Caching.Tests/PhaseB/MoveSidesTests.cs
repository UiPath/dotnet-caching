using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

/// <summary>
/// Side capabilities, views, barriers and connection counters of the instance move. Beyond the shared test server: MOVE_SIDES_MASTER and MOVE_SIDES_REPLICA
/// (a master and its replica, the replica with the debug command enabled), MOVE_SIDES_LIMITED (a user without SCAN, ROLE, CONFIG, INFO and WAIT) and
/// MOVE_SIDES_NOMULTI (a user without MULTI and WATCH).
/// </summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoveSidesTests(RedisContainerFixture fixture)
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.";
    private static readonly MasterCaps GoodMaster = new(new DnsEndPoint("m", 6379), new Version(7, 4), Scripts: true, Transactions: true, Hashes: true, Wait: true, 1, "noeviction");
    private static readonly SideCaps Good = Side(GoodMaster);

    private static string? MasterWithReplica => Environment.GetEnvironmentVariable("MOVE_SIDES_MASTER");

    private static string? Replica => Environment.GetEnvironmentVariable("MOVE_SIDES_REPLICA");

    private static string? Limited => Environment.GetEnvironmentVariable("MOVE_SIDES_LIMITED");

    private static string? NoMulti => Environment.GetEnvironmentVariable("MOVE_SIDES_NOMULTI");

    [Fact]
    public void S3_G6_is_the_conjunction_of_both_sides_and_names_the_failing_side()
    {
        SideCaps.Gate(Good, Good).Should().BeEmpty();
        SideCaps.Gate(Good, Side(GoodMaster with { Wait = false })).Should().BeEmpty("WAIT needs an approval, not the gate");
        SideCaps.Gate(Good, Side(GoodMaster with { Scripts = false })).Should().BeEmpty("a side without scripts takes the fallback on 6.0.9");

        var failures = SideCaps.Gate(
            Side(GoodMaster with { Scripts = false, Version = new Version(6, 0, 8) }),
            Side(GoodMaster with { EvictionPolicy = "allkeys-lru" }, ScanMode.None));

        failures.Should().HaveCount(3);
        failures.Should().Contain(f => f.StartsWith("src:", StringComparison.Ordinal) && f.Contains("fallback", StringComparison.Ordinal));
        failures.Should().Contain(f => f.StartsWith("dst:", StringComparison.Ordinal) && f.Contains("scan", StringComparison.Ordinal));
        failures.Should().Contain(f => f.StartsWith("dst:", StringComparison.Ordinal) && f.Contains("allkeys-lru", StringComparison.Ordinal));
    }

    [Fact]
    public void A_master_without_scripts_needs_the_fallback_and_therefore_transactions_and_a_new_enough_server()
    {
        GoodMaster.Failures("src").Should().BeEmpty();
        (GoodMaster with { Scripts = false }).Failures("src").Should().BeEmpty();
        (GoodMaster with { Scripts = false, Transactions = false }).Failures("src").Should().ContainSingle().Which.Should().Contain("m:6379").And.Contain("transactions");
        (GoodMaster with { Hashes = false }).Failures("src").Should().ContainSingle().Which.Should().Contain("hash");
        (GoodMaster with { Hashes = false, Scripts = false, Transactions = false }).Failures("dst").Should().HaveCount(2);
    }

    [Fact]
    public void One_restricted_or_older_master_decides_for_the_side()
    {
        var other = GoodMaster with { EndPoint = new DnsEndPoint("n", 6379), Version = new Version(6, 0, 8), Scripts = false, Wait = false, Replicas = null, EvictionPolicy = "allkeys-lru" };
        var side = new SideCaps([GoodMaster, other], ScanMode.PerShard, SlotEnforced: false, []);

        side.Version.Should().Be(new Version(6, 0, 8));
        side.Scripts.Should().BeFalse();
        side.Wait.Should().BeFalse();
        side.Replicas.Should().BeNull("a master that refused ROLE makes the count unknown");
        side.EvictionPolicy.Should().Be("allkeys-lru");
        side.Failures("src").Should().Contain(f => f.Contains("n:6379", StringComparison.Ordinal));
        new SideCaps([GoodMaster, GoodMaster with { Replicas = 0 }], ScanMode.PerShard, false, []).Replicas.Should().Be(0);
        new SideCaps([GoodMaster, GoodMaster with { EvictionPolicy = null }], ScanMode.PerShard, false, []).EvictionPolicy.Should().BeNull();
    }

    [Theory]
    [InlineData("noeviction", true)]
    [InlineData("volatile-lru", true)]
    [InlineData("allkeys-lfu", false)]
    [InlineData(null, null)]
    public void Eviction_is_safe_only_for_policies_that_never_evict_a_key_without_a_ttl(string? policy, bool? safe) =>
        (GoodMaster with { EvictionPolicy = policy }).EvictionSafe.Should().Be(safe);

    [Fact]
    public async Task A_plain_server_probes_as_scripts_per_shard_scan_no_replica_and_no_wait()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
        var replicas = await mux.GetServer(mux.GetEndPoints()[0]).RoleAsync() is Role.Master m ? m.Replicas.Count : 0;
        Assert.SkipUnless(replicas == 0, "The shared test server has replicas.");

        var caps = await SideProbe.ProbeAsync(mux.GetDatabase(), "sidetest:mg:", "sidetest:*", cancellationToken: TestContext.Current.CancellationToken);

        caps.Scripts.Should().BeTrue();
        caps.Replicas.Should().Be(0);
        caps.Wait.Should().BeFalse("no replica answers, and the probe does not wait for one that does not exist");
        caps.Scan.Should().Be(ScanMode.PerShard);
        caps.SlotEnforced.Should().BeFalse();
        caps.EvictionPolicy.Should().Be("noeviction");
        caps.Masters.Should().ContainSingle().Which.Should().Match<MasterCaps>(m => m.Hashes && m.Transactions);
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

        var caps = await SideProbe.ProbeAsync(refusing, "sidetest:mg:", "sidetest:*", cancellationToken: TestContext.Current.CancellationToken);

        caps.Scripts.Should().BeFalse();
        caps.Denied.Should().Contain("EVAL");
        caps.SlotEnforced.Should().BeTrue("without scripts the probe assumes slots are enforced, which is always safe");
        caps.MoveScriptsOn(refusing).UsesScripts.Should().BeFalse("this side takes the fallback on its own");
    }

    [Fact]
    public async Task A_user_denied_the_probed_commands_reports_each_denial_as_a_capability()
    {
        Assert.SkipUnless(Limited is not null, "Set MOVE_SIDES_LIMITED to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(Limited!);

        var caps = await SideProbe.ProbeAsync(mux.GetDatabase(), "sidetest:mg:", "sidetest:*", cancellationToken: TestContext.Current.CancellationToken);

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

        var caps = await SideProbe.ProbeAsync(db, "sidetest:mg:", "sidetest:*", cancellationToken: TestContext.Current.CancellationToken);
        caps.Wait.Should().BeTrue();
        caps.Replicas.Should().Be(1);

        var barrier = new ServerBarrier(db, "sidetest:k", TimeProvider.System, replicaFree: false);
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
        var barrier = new ServerBarrier(db, "sidetest:k", TimeProvider.System, replicaFree: false) { Timeout = TimeSpan.FromMilliseconds(200), Retries = 2 };

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

        var db = mux.GetDatabase();

        (await new ServerBarrier(db, "sidetest:k", TimeProvider.System, replicaFree: true).WaitAsync(() => Task.CompletedTask, wayBack: false))
            .Should().Be(BarrierOutcome.NoReplica);
        (await new ServerBarrier(db, "sidetest:k", TimeProvider.System, replicaFree: false) { Retries = 1 }.WaitAsync(() => Task.CompletedTask, wayBack: false))
            .Should().Be(BarrierOutcome.NotConfirmed, "a deployment not configured as replica-free expects a replica, whatever ROLE lists now");
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
    public async Task A_user_denied_multi_and_watch_probes_as_scripts_without_transactions()
    {
        Assert.SkipUnless(NoMulti is not null, "Set MOVE_SIDES_NOMULTI to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(NoMulti!);

        var caps = await SideProbe.ProbeAsync(mux.GetDatabase(), "sidetest:mg:", "sidetest:*", cancellationToken: TestContext.Current.CancellationToken);

        var master = caps.Masters.Should().ContainSingle().Subject;
        master.Transactions.Should().BeFalse();
        master.Scripts.Should().BeTrue();
        master.Hashes.Should().BeTrue();
        caps.Denied.Should().Contain("MULTI/WATCH");
        master.Failures("dst").Should().BeEmpty("scripts run, so the fallback is not needed");
    }

    [Fact]
    public async Task A_user_denied_eval_and_multi_cannot_run_either_path()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var noMulti = InterceptingProxy.Wrap(mux.GetDatabase(), (method, _) =>
            method.Name == nameof(IDatabase.CreateTransaction)
                ? throw new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM User u has no permissions to run the 'multi' command")
                : method.Name == nameof(IDatabase.ScriptEvaluateAsync)
                    ? Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM User u has no permissions to run the 'eval' command"))
                    : InterceptingProxy.Proceed);

        var caps = await SideProbe.ProbeAsync(noMulti, "sidetest:mg:", "sidetest:*", cancellationToken: TestContext.Current.CancellationToken);

        caps.Denied.Should().Contain(["EVAL", "MULTI/WATCH"]);
        caps.Failures("src").Should().ContainSingle().Which.Should().Contain("neither scripts nor the fallback");
    }

    [Fact]
    public async Task A_denial_inside_the_script_is_told_from_a_refusal_of_eval()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var inside = InterceptingProxy.Wrap(mux.GetDatabase(), (method, _) =>
            method.Name == nameof(IDatabase.ScriptEvaluateAsync)
                ? Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "ERR Error running script (call to f_1): @user_script:1: NOPERM User u has no permissions to run the 'hdel' command"))
                : InterceptingProxy.Proceed);

        var caps = await SideProbe.ProbeAsync(inside, "sidetest:mg:", "sidetest:*", cancellationToken: TestContext.Current.CancellationToken);

        caps.Scripts.Should().BeFalse();
        caps.Denied.Should().NotContain("EVAL");
        caps.Denied.Should().ContainSingle(d => d.StartsWith("script:", StringComparison.Ordinal) && d.Contains("hdel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scan_that_needs_more_than_a_thousand_steps_still_completes_per_shard()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var keys = Enumerable.Range(0, 15000).Select(i => (RedisKey)$"sidetest:bulk:{i}").ToArray();
        try
        {
            var batch = db.CreateBatch();
            var writes = keys.Select(k => batch.StringSetAsync(k, "v")).ToArray();
            batch.Execute();
            await Task.WhenAll(writes);

            var caps = await SideProbe.ProbeAsync(db, "sidetest:mg:", "sidetest:bulk:*", cancellationToken: TestContext.Current.CancellationToken, scanCount: 10);

            caps.Scan.Should().Be(ScanMode.PerShard);
        }
        finally
        {
            await db.KeyDeleteAsync(keys);
        }
    }

    [Fact]
    public async Task A_cancelled_probe_stops_instead_of_reporting_a_failed_scan()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await FluentActions.Awaiting(() => SideProbe.ProbeAsync(mux.GetDatabase(), "sidetest:mg:", "sidetest:*", cancellationToken: cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_views_and_counters_follow_a_reconnect_of_either_connector()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var ct = TestContext.Current.CancellationToken;
        using var src = NewConnector();
        using var dst = NewConnector();
        await src.ConnectAsync(ct);
        await dst.ConnectAsync(ct);
        await using var killer = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
        using var views = await MoveViews.CreateAsync(src, dst, string.Empty, NullLogger.Instance, ct);

        foreach (var (connector, current) in new (IRedisConnector, Func<IConnectionMultiplexer>)[] { (dst, () => views.Destination.Multiplexer), (src, () => views.SourcePrefixed.Multiplexer) })
        {
            var mark = views.Changes.Current;
            var before = current();

            connector.ForceReconnect();
            await WaitUntilAsync(() => !ReferenceEquals(current(), before) && views.Changes.ChangedSince(mark), TimeSpan.FromSeconds(30), ct);

            await views.Destination.PingAsync();
            await views.SourcePrefixed.PingAsync();
            views.SourceRaw.Multiplexer.Should().BeSameAs(views.SourcePrefixed.Multiplexer);

            // The new connection's failure moves the counter again: its events are subscribed.
            var afterReconnect = views.Changes.Current;
            var id = (long)await current().GetServer(current().GetEndPoints()[0]).ExecuteAsync("CLIENT", "ID");
            await killer.GetServer(killer.GetEndPoints()[0]).ExecuteAsync("CLIENT", "KILL", "ID", id);
            await WaitUntilAsync(() => views.Changes.ChangedSince(afterReconnect), TimeSpan.FromSeconds(30), ct);
        }
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
        await WaitUntilAsync(() => changes.ChangedSince(mark), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        changes.ChangedSince(mark).Should().BeTrue();
    }

    private static SideCaps Side(MasterCaps master, ScanMode scan = ScanMode.PerShard) => new([master], scan, SlotEnforced: false, []);

    /// <summary>A bounded wait on a condition, on the task scheduler's own clock instead of the wall clock.</summary>
    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan limit, CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                while (!condition())
                {
                    await Task.Delay(10, cancellationToken);
                }
            },
            cancellationToken).WaitAsync(limit, cancellationToken);

    private RedisConnector NewConnector()
    {
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = $"{fixture.ConnectionString},allowAdmin=true", EnableHangDetection = false });
        var optionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options);
        var factory = new ConnectionMultiplexerFactory(options, NullRedisProfiler.Instance);
        return new RedisConnector(NullTelemetryProvider.Instance, optionsProvider, factory, options);
    }
}
