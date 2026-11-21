using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
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
/// (a master and its replica, the replica with the debug command enabled), MOVE_SIDES_LIMITED (a user without SCAN, ROLE, CONFIG, INFO and WAIT),
/// MOVE_SIDES_NOMULTI (a user without MULTI and WATCH) and MOVE_SIDES_NOPEXPIREAT (a user without PEXPIREAT).
/// </summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoveSidesTests(RedisContainerFixture fixture)
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.";
    private static readonly MasterCaps GoodMaster = new(new DnsEndPoint("m", 6379), new Version(7, 4), Scripts: true, Transactions: true, Hashes: true, Wait: true, 1, "noeviction");
    private static readonly SideCaps Good = Side(GoodMaster);
    private static readonly EndPoint A = new DnsEndPoint("a", 6379);
    private static readonly EndPoint B = new DnsEndPoint("b", 6379);
    private static readonly EndPoint C = new DnsEndPoint("c", 6379);

    private static string? MasterWithReplica => Environment.GetEnvironmentVariable("MOVE_SIDES_MASTER");

    private static string? Replica => Environment.GetEnvironmentVariable("MOVE_SIDES_REPLICA");

    private static string? Limited => Environment.GetEnvironmentVariable("MOVE_SIDES_LIMITED");

    private static string? NoMulti => Environment.GetEnvironmentVariable("MOVE_SIDES_NOMULTI");

    private static string? NoPexpireat => Environment.GetEnvironmentVariable("MOVE_SIDES_NOPEXPIREAT");

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
        var side = new SideCaps([GoodMaster, other], ScanMode.PerShard, SlotEnforced: false, [], []);

        side.Version.Should().Be(new Version(6, 0, 8));
        side.Scripts.Should().BeFalse();
        side.Wait.Should().BeFalse();
        side.Replicas.Should().BeNull("a master that refused ROLE makes the count unknown");
        side.EvictionPolicy.Should().Be("allkeys-lru");
        side.Failures("src").Should().Contain(f => f.Contains("n:6379", StringComparison.Ordinal));
        new SideCaps([GoodMaster, GoodMaster with { Replicas = 0 }], ScanMode.PerShard, false, [], []).Replicas.Should().Be(0);
        new SideCaps([GoodMaster, GoodMaster with { EvictionPolicy = null }], ScanMode.PerShard, false, [], []).EvictionPolicy.Should().BeNull();
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
        DeniedCommands(caps).Should().Contain("EVAL");
        caps.Denied.Should().OnlyContain(d => d.Contains("@", StringComparison.Ordinal), "every denial names the endpoint");
        caps.SlotEnforced.Should().BeTrue("without scripts the probe assumes slots are enforced, which is always safe");
        caps.MoveScriptsOn(refusing).UsesScripts.Should().BeFalse("this side takes the fallback on its own");
    }

    [Fact]
    public async Task A_user_denied_the_probed_commands_reports_each_denial_as_a_capability()
    {
        Assert.SkipUnless(Limited is not null, "Set MOVE_SIDES_LIMITED to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{Limited},allowAdmin=true");

        var caps = await SideProbe.ProbeAsync(mux.GetDatabase(), "sidetest:mg:", "sidetest:*", cancellationToken: TestContext.Current.CancellationToken);

        caps.Scan.Should().Be(ScanMode.None);
        caps.Replicas.Should().BeNull();
        caps.EvictionPolicy.Should().BeNull();
        caps.Notes("dst").Should().ContainSingle().Which.Should().Be("dst: eviction policy unknown, needs attestation (G12)");
        DeniedCommands(caps).Should().Contain(["SCAN", "ROLE"]);
        caps.Denied.Should().OnlyContain(d => d.Contains("@", StringComparison.Ordinal), "every denial names the endpoint");
        caps.Failures("dst").Should().HaveCount(2).And.Contain(f => f.Contains("scan", StringComparison.Ordinal)).And.Contain(f => f.Contains("denies INFO", StringComparison.Ordinal), "an unknown eviction policy is a note for G12, not a gate failure");
    }

    [Fact]
    public async Task A_master_with_a_replica_probes_wait_and_its_barrier_confirms()
    {
        Assert.SkipUnless(MasterWithReplica is not null, "Set MOVE_SIDES_MASTER to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{MasterWithReplica},allowAdmin=true");
        var db = mux.GetDatabase();

        var caps = await SideProbe.ProbeAsync(db, "sidetest:mg:", "sidetest:*", cancellationToken: TestContext.Current.CancellationToken);
        caps.Wait.Should().BeTrue();
        caps.Replicas.Should().Be(1);
        caps.Failures("src").Should().BeEmpty();

        var barrier = new ServerBarrier(db, "sidetest:k", TimeProvider.System, replicaFree: false);
        (await barrier.WriteAndWaitAsync(transaction => transaction.StringSetAsync("sidetest:k", "v"), wayBack: false)).Should().Be(BarrierOutcome.Confirmed);
    }

    [Fact]
    public async Task A_replica_that_cannot_acknowledge_leaves_the_barrier_unconfirmed_and_a_way_back_then_skips_it()
    {
        Assert.SkipUnless(MasterWithReplica is not null && Replica is not null, "Set MOVE_SIDES_MASTER and MOVE_SIDES_REPLICA to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{MasterWithReplica},allowAdmin=true,syncTimeout=10000");
        await using var replica = await ConnectionMultiplexer.ConnectAsync($"{Replica},allowAdmin=true,syncTimeout=10000");
        var db = mux.GetDatabase();
        var written = 0;
        var barrier = new ServerBarrier(db, "sidetest:k", TimeProvider.System, replicaFree: false) { Timeout = TimeSpan.FromMilliseconds(200), Retries = 2 };

        var sleeping = replica.GetServer(replica.GetEndPoints()[0]).ExecuteAsync("DEBUG", "SLEEP", "2");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var outcome = await barrier.WriteAndWaitAsync(
            transaction =>
            {
                written++;
                return transaction.StringSetAsync("sidetest:k", "v");
            },
            wayBack: false);

        outcome.Should().Be(BarrierOutcome.NotConfirmed, "0 or a timeout is not a confirmation");
        written.Should().Be(3, "attempt 0 and one transaction per retry, with no extra backoff");
        (await barrier.WriteAndWaitAsync(transaction => transaction.StringSetAsync("sidetest:k", "v"), wayBack: true)).Should().Be(BarrierOutcome.SkippedAfterRecentFailure);
        await sleeping;
    }

    [Fact]
    public async Task A_master_without_a_replica_needs_no_barrier()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
        var replicas = await mux.GetServer(mux.GetEndPoints()[0]).RoleAsync() is Role.Master m ? m.Replicas.Count : 0;
        Assert.SkipUnless(replicas == 0, "The shared test server has replicas.");

        var db = mux.GetDatabase();

        var free = new ServerBarrier(db, "sidetest:k", TimeProvider.System, replicaFree: true);
        var expecting = new ServerBarrier(db, "sidetest:k", TimeProvider.System, replicaFree: false) { Retries = 1 };

        (await free.WriteAndWaitAsync(transaction => transaction.StringSetAsync("sidetest:k", "v"), wayBack: false))
            .Should().Be(BarrierOutcome.NoReplica);
        (await expecting.WriteAndWaitAsync(transaction => transaction.StringSetAsync("sidetest:k", "v"), wayBack: false))
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

        var name = $"viewsraw:{Guid.NewGuid():N}";
        try
        {
            await views.SourceRaw.StringSetAsync($"{{tag}}:{name}", "raw");

            (await src.GetDatabase().StringGetAsync($"{{tag}}:{name}")).ToString().Should().Be("raw", "SourceRaw wrote the key as given, with no prefix added");
            (await src.GetDatabase().KeyExistsAsync($"{{tag}}:{{tag}}:{name}")).Should().BeFalse("SourceRaw added no second prefix");
            (await views.SourcePrefixed.StringGetAsync(name)).ToString().Should().Be("raw", "the prefixed view adds the prefix the raw write already holds");
        }
        finally
        {
            await src.GetDatabase().KeyDeleteAsync([$"{{tag}}:{name}", $"{{tag}}:{{tag}}:{name}"]);
        }
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
        DeniedCommands(caps).Should().Contain("MULTI/WATCH");
        master.Failures("dst").Should().BeEmpty("scripts run, so the fallback is not needed");
    }

    [Fact]
    public async Task A_user_denied_eval_and_multi_cannot_run_either_path()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
        var noMulti = InterceptingProxy.Wrap(mux.GetDatabase(), (method, _) =>
            method.Name == nameof(IDatabase.CreateTransaction)
                ? throw new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM User u has no permissions to run the 'multi' command")
                : method.Name == nameof(IDatabase.ScriptEvaluateAsync)
                    ? Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM User u has no permissions to run the 'eval' command"))
                    : InterceptingProxy.Proceed);

        var caps = await SideProbe.ProbeAsync(noMulti, "sidetest:mg:", "sidetest:*", cancellationToken: TestContext.Current.CancellationToken);

        DeniedCommands(caps).Should().Contain(["EVAL", "MULTI/WATCH"]);
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
        DeniedCommands(caps).Should().NotContain("EVAL");
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
            var steps = 0;

            var caps = await SideProbe.ProbeAsync(CountingScans(mux, db, () => steps++), "sidetest:mg:", "sidetest:bulk:*", scanBudget: null, cancellationToken: TestContext.Current.CancellationToken, scanCount: 10);

            caps.Scan.Should().Be(ScanMode.PerShard);
            steps.Should().BeGreaterThan(1000, "the scan walked every step of the keyspace with COUNT 10");
        }
        finally
        {
            await db.KeyDeleteAsync(keys);
        }
    }

    [Fact]
    public async Task A_scan_that_outlasts_its_budget_is_incomplete_and_the_default_budget_leaves_a_small_scan_whole()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
        var db = mux.GetDatabase();
        var keys = Enumerable.Range(0, 300).Select(i => (RedisKey)$"sidetest:budget:{i}").ToArray();
        try
        {
            await Task.WhenAll(keys.Select(k => db.StringSetAsync(k, "v")));
            var ct = TestContext.Current.CancellationToken;

            var bounded = await SideProbe.ProbeAsync(db, "sidetest:mg:", "sidetest:budget:*", scanBudget: TimeSpan.Zero, cancellationToken: ct, scanCount: 1);
            var whole = await SideProbe.ProbeAsync(db, "sidetest:mg:", "sidetest:budget:*", cancellationToken: ct);

            bounded.Scan.Should().Be(ScanMode.Incomplete);
            bounded.Failures("src").Should().BeEmpty("an incomplete scan probe is not a gate failure");
            bounded.Notes("src").Should().Contain("src: scan probe incomplete; the move's first scan traverses fully");
            whole.Scan.Should().Be(ScanMode.PerShard);
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
    public async Task A_cancellation_inside_the_scan_throws_and_stops_the_walk()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var keys = Enumerable.Range(0, 300).Select(i => (RedisKey)$"sidetest:cancel:{i}").ToArray();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await Task.WhenAll(keys.Select(k => db.StringSetAsync(k, "v")));
            var steps = 0;

            await FluentActions.Awaiting(() => SideProbe.ProbeAsync(
                    CountingScans(mux, db, () =>
                    {
                        if (++steps == 3)
                        {
                            cancellation.Cancel();
                        }
                    }),
                    "sidetest:mg:",
                    "sidetest:cancel:*",
                    scanBudget: null,
                    cancellationToken: cancellation.Token,
                    scanCount: 1))
                .Should().ThrowAsync<OperationCanceledException>();
            steps.Should().Be(3, "no step ran after the cancellation");
        }
        finally
        {
            await db.KeyDeleteAsync(keys);
        }
    }

    [Fact]
    public async Task A_user_denied_one_command_the_move_uses_reports_it_by_name_and_endpoint()
    {
        Assert.SkipUnless(NoPexpireat is not null, "Set MOVE_SIDES_NOPEXPIREAT to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(NoPexpireat!);

        var caps = await SideProbe.ProbeAsync(mux.GetDatabase(), "sidetest:mg:", "sidetest:*", cancellationToken: TestContext.Current.CancellationToken);

        var master = caps.Masters.Should().ContainSingle().Subject;
        master.Scripts.Should().BeFalse();
        master.Transactions.Should().BeFalse();
        caps.Denied.Should().Contain(d => d.StartsWith("script:pexpireat@", StringComparison.OrdinalIgnoreCase));
        caps.Denied.Should().Contain(d => d.StartsWith("transaction:", StringComparison.Ordinal), "the server aborts the EXEC without naming the command, unlike the script");
        caps.Failures("src").Should().Contain(f => f.Contains("neither scripts nor the fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void A_side_that_cannot_use_scripts_needs_the_fallback_on_every_master()
    {
        var withoutFallback = GoodMaster with { EndPoint = new DnsEndPoint("n", 6379), Transactions = false };
        var old = GoodMaster with { EndPoint = new DnsEndPoint("o", 6379), Version = new Version(6, 0, 8) };
        var withoutScripts = GoodMaster with { EndPoint = new DnsEndPoint("p", 6379), Scripts = false };

        new SideCaps([withoutFallback, withoutScripts], ScanMode.PerShard, false, [], []).Failures("src")
            .Should().ContainSingle().Which.Should().Contain("n:6379").And.Contain("fallback");
        new SideCaps([old, withoutScripts], ScanMode.PerShard, false, [], []).Failures("src")
            .Should().ContainSingle().Which.Should().Contain("o:6379");
        new SideCaps([withoutFallback, GoodMaster], ScanMode.PerShard, false, [], []).Failures("src")
            .Should().BeEmpty("every master runs scripts, so the side never needs the fallback");
    }

    [Fact]
    public void An_unreachable_primary_fails_the_side_and_unknown_eviction_and_an_incomplete_scan_are_notes()
    {
        var unreachable = new SideCaps([GoodMaster], ScanMode.PerShard, false, [], [new DnsEndPoint("down", 6379)]);

        unreachable.Failures("dst").Should().ContainSingle().Which.Should().Be("dst: Unspecified/down:6379 unreachable");
        Good.Notes("dst").Should().BeEmpty();
        Side(GoodMaster with { EvictionPolicy = null }).Notes("dst").Should().Equal("dst: eviction policy unknown, needs attestation (G12)");
        Side(GoodMaster, ScanMode.Incomplete).Notes("src").Should().Equal("src: scan probe incomplete; the move's first scan traverses fully");
        SideCaps.Gate(Side(GoodMaster, ScanMode.Incomplete), Good).Should().BeEmpty();
    }

    [Fact]
    public async Task A_probe_of_two_primaries_covers_each_with_its_own_keys_and_reports_a_disconnected_one()
    {
        var side = new FakeSide(new FakePrimary(A, Replicas: 1), new FakePrimary(B, Replicas: 0), new FakePrimary(C, Connected: false));
        var ct = TestContext.Current.CancellationToken;

        var caps = await SideProbe.ProbeAsync(side.Database, "side:", "side:*", cancellationToken: ct);

        caps.Masters.Select(static m => m.EndPoint).Should().Equal(A, B);
        caps.Masters.Select(static m => m.Replicas).Should().Equal(1, 0);
        caps.Masters.Select(static m => m.Wait).Should().Equal(true, false);
        caps.Replicas.Should().Be(0);
        caps.Wait.Should().BeFalse();
        caps.Unreachable.Should().Equal(C);
        caps.Failures("src").Should().Equal($"src: {C} unreachable");
        side.Scanned.Should().BeEquivalentTo([A, B], "the disconnected primary is reported, never silently scanned or dropped");
        side.WrittenKeys.Select(side.Owner).Distinct().Should().BeEquivalentTo([A, B], "each master was probed with a key it owns");

        await SideProbe.ProbeAsync(side.Database, "side:", "side:*", cancellationToken: ct);

        var second = side.WrittenKeys.Skip(side.WrittenKeys.Count / 2).ToHashSet();
        side.WrittenKeys.Take(side.WrittenKeys.Count / 2).Should().NotIntersectWith(second, "a second call uses keys of its own");
        side.WrittenKeys.Should().OnlyContain(k => Regex.IsMatch(k, @"^side:probe:\{[^}]+\}:[0-9a-f]{32}:[ab]$"), "keys keep the slot tag and carry a nonce");
    }

    [Fact]
    public async Task A_role_denied_leaves_wait_to_the_wait_probe_itself()
    {
        var denied = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM User u has no permissions to run the 'role' command");
        var ct = TestContext.Current.CancellationToken;

        var answering = await SideProbe.ProbeAsync(new FakeSide(new FakePrimary(A, Role: denied, Wait: 1)).Database, "side:", "side:*", cancellationToken: ct);
        var silent = await SideProbe.ProbeAsync(new FakeSide(new FakePrimary(A, Role: denied, Wait: 0)).Database, "side:", "side:*", cancellationToken: ct);

        answering.Masters.Single().Replicas.Should().BeNull();
        answering.Masters.Single().Wait.Should().BeTrue();
        silent.Masters.Single().Wait.Should().BeFalse("WAIT gave 0 five times: it is not assumed available");
        answering.Denied.Should().Equal($"ROLE@{A}");
    }

    [Fact]
    public async Task A_client_timeout_on_wait_the_eviction_reads_or_the_scan_is_not_an_error()
    {
        var timeout = new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Sent);
        var side = new FakeSide(new FakePrimary(A, Replicas: 1, Wait: timeout, Config: timeout, Info: timeout, Scan: timeout));

        var caps = await SideProbe.ProbeAsync(side.Database, "side:", "side:*", cancellationToken: TestContext.Current.CancellationToken);

        caps.Masters.Single().Wait.Should().BeFalse();
        caps.EvictionPolicy.Should().BeNull();
        caps.Scan.Should().Be(ScanMode.None);
        caps.Notes("src").Should().Contain("src: eviction policy unknown, needs attestation (G12)");
    }

    [Fact]
    public async Task An_out_of_memory_refusal_is_an_error_not_a_denial()
    {
        var oom = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "OOM command not allowed when used memory > 'maxmemory'.");
        var side = new FakeSide(new FakePrimary(A, Replicas: 1)) { DatabaseFault = oom };

        await FluentActions.Awaiting(() => SideProbe.ProbeAsync(side.Database, "side:", "side:*", cancellationToken: TestContext.Current.CancellationToken))
            .Should().ThrowAsync<RedisServerException>().WithMessage("OOM*");
    }

    [Fact]
    public void A_connector_that_reconnects_while_the_counter_subscribes_is_followed()
    {
        var first = new EventCountingMultiplexer();
        var second = new EventCountingMultiplexer();
        var third = new EventCountingMultiplexer();
        var connector = new ReconnectingConnector(first) { ReplaceOnSubscribe = second };

        using var changes = new ConnectionChanges(connector);

        first.Attached.Should().Be(0, "the replaced multiplexer was never attached");
        second.Attached.Should().Be(1, "the multiplexer current after the subscription is the one followed");

        var mark = changes.Current;
        connector.Reconnect(third);

        changes.ChangedSince(mark).Should().BeTrue();
        second.Attached.Should().Be(0);
        third.Attached.Should().Be(1);
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

    private static IEnumerable<string> DeniedCommands(SideCaps caps) => caps.Denied.Select(static d => d[..d.IndexOf('@', StringComparison.Ordinal)]);

    /// <summary>The database whose multiplexer shows every server wrapped so each SCAN step is reported to <paramref name="onScan"/>.</summary>
    private static IDatabase CountingScans(IConnectionMultiplexer mux, IDatabase db, Action onScan)
    {
        var servers = mux.GetServers().Select(server => InterceptingProxy.Wrap(server, (method, args) =>
        {
            if (method.Name == nameof(IServer.ExecuteAsync) && args[0] is "SCAN")
            {
                onScan();
            }

            return InterceptingProxy.Proceed;
        })).ToArray();
        var wrapped = InterceptingProxy.Wrap(mux, (method, _) => method.Name == nameof(IConnectionMultiplexer.GetServers) ? servers : InterceptingProxy.Proceed);
        return InterceptingProxy.Wrap(db, (method, _) => method.Name == "get_Multiplexer" ? wrapped : InterceptingProxy.Proceed);
    }

    private static SideCaps Side(MasterCaps master, ScanMode scan = ScanMode.PerShard) => new([master], scan, SlotEnforced: false, [], []);

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
