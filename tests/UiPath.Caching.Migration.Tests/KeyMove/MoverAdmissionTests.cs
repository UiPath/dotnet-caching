using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Admission, the scan and what a pass may call finished: a state that moves mid-batch, a cursor of another server, a failed key, the database, the pace, and the alerts.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoverAdmissionTests(RedisFixture fixture)
{
    private static readonly MoverOptions Small = new()
    {
        ScanCount = 5,
        PollInterval = TimeSpan.FromMilliseconds(40),
        MinScanInterval = TimeSpan.FromMilliseconds(1),
        PassPause = TimeSpan.FromHours(1),
        LeaseTtl = TimeSpan.FromSeconds(5),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_gate_that_moves_to_a_new_identity_in_the_middle_of_a_batch_leaves_the_rest_of_the_batch_in_L()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var keys = Enumerable.Range(0, 20).Select(i => $"k{i:00}").ToArray();
        await kit.SeedAsync(keys);
        var seen = 0;
        var target = new CountingTarget(kit.Target)
        {
            Before = async (_, _) =>
            {
                if (Interlocked.Increment(ref seen) == 5)
                {
                    await kit.Kit.MoveGateAsync();
                }

                return null;
            },
        };
        var host = kit.Host(MoverKit.Quick, _ => target);

        host.Start();
        try
        {
            await kit.Kit.Time.NextTimerAsync(Ct);
        }
        finally
        {
            await host.StopAsync();
        }

        target.TotalMoved.Should().Be(4, "from the fifth key the admitted unit runs under another identity, which the runner refuses");
        var left = 0;
        foreach (var key in keys)
        {
            left += await kit.Kit.ExistsAsync(kit.Kit.L(key)) ? 1 : 0;
        }

        left.Should().Be(16);
        (await kit.PassedAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Rules_that_leave_forward_S3_in_the_middle_of_a_batch_stop_it_before_the_next_key()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var keys = Enumerable.Range(0, 20).Select(i => $"k{i:00}").ToArray();
        await kit.SeedAsync(keys);
        var seen = 0;
        var target = new CountingTarget(kit.Target)
        {
            Before = async (_, _) =>
            {
                if (Interlocked.Increment(ref seen) == 5)
                {
                    kit.Kit.ApplyRules("S4");
                }

                return await Task.FromResult<MoveResult?>(null);
            },
        };
        var host = kit.Host(MoverKit.Quick, _ => target);

        host.Start();
        try
        {
            await kit.Kit.Time.NextTimerAsync(Ct);
        }
        finally
        {
            await host.StopAsync();
        }

        target.Total.Should().Be(5, "no key is scheduled once the rules are no longer forward S3");
        (await kit.PassedAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_replica_cursor_is_never_sent_to_the_primary_and_the_pass_starts_again_from_zero_there()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var keys = Enumerable.Range(0, 40).Select(i => $"k{i}").ToArray();
        await kit.SeedAsync(keys);
        ScanRecordingServer? replica = null;
        ScanRecordingServer? primary = null;
        var units = new ProxyUnits(kit.Units, u => u with
        {
            Primary = ScanRecordingServer.Over(u.Primary, out primary),
            Replica = ScanRecordingServer.Over(u.Primary, out replica, new System.Net.DnsEndPoint("replica.invalid", 6380)),
        });
        var moves = new CountingTarget(kit.Target);
        var host = kit.Host(Small, _ => moves, units: units);
        await units.ListAsync(Ct);
        replica!.FailAfter = 1;

        await kit.RunOnePassAsync(host);

        replica.Cursors.Should().HaveCount(2).And.Subject.Last().Should().NotBe("0", "the replica failed on a cursor it had issued");
        primary!.Cursors[0].Should().Be("0", "the cursor of the replica is of no use to the primary");
        foreach (var key in keys)
        {
            (await kit.Kit.ExistsAsync(kit.Kit.N(key))).Should().BeTrue(key);
        }
    }

    [Fact]
    public async Task A_stored_cursor_of_another_server_is_not_resumed()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var keys = Enumerable.Range(0, 20).Select(i => $"k{i}").ToArray();
        await kit.SeedAsync(keys);
        var unit = (await kit.UnitIdsAsync()).Single();
        var patterns = MoverUnitRunner.HashOf(kit.Space.Patterns(kit.Kit.Group, MoveSide.Legacy));
        await kit.Db.StringSetAsync(kit.Kit.Names.UnitCursor(kit.Kit.Group, unit, "fwd", MoverKit.MoveId), $"0|123456|{kit.Kit.Id}|another-server:6379#run-1|{patterns}|0");
        ScanRecordingServer? scans = null;
        var host = kit.Host(Small, units: new ProxyUnits(kit.Units, u => u with { Primary = ScanRecordingServer.Over(u.Primary, out scans) }));

        await kit.RunOnePassAsync(host);

        scans!.Cursors[0].Should().Be("0");
        foreach (var key in keys)
        {
            (await kit.Kit.ExistsAsync(kit.Kit.N(key))).Should().BeTrue(key);
        }
    }

    [Fact]
    public async Task A_pass_in_which_a_key_failed_is_not_finished_and_the_next_clean_pass_is()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("a", "b", "c");
        var unit = (await kit.UnitIdsAsync()).Single();
        var passKey = kit.Kit.Names.UnitPass(kit.Kit.Group, unit, "fwd", MoverKit.MoveId);
        bool? recordedAtSecondPass = null;
        var flaky = new CountingTarget(kit.Target)
        {
            Before = async (key, n) =>
            {
                if (key != "b")
                {
                    return null;
                }

                if (n == 1)
                {
                    return MoveResult.Skip("unavailable");
                }

                recordedAtSecondPass ??= await kit.Db.KeyExistsAsync(passKey);
                return null;
            },
        };
        var host = kit.Host(new MoverOptions { PollInterval = TimeSpan.FromMilliseconds(40), MinScanInterval = TimeSpan.FromMilliseconds(1), PassPause = TimeSpan.FromMilliseconds(400) }, _ => flaky);

        host.Start();
        try
        {
            await kit.WaitPassAttemptAsync(host);
            await kit.Db.KeyExpireAsync(kit.Kit.Names.UnitPace(kit.Kit.Group, unit, "fwd", MoverKit.MoveId), DateTimeOffset.UnixEpoch.UtcDateTime);
            kit.Kit.Time.Advance(TimeSpan.FromMilliseconds(400));
            await kit.WaitForPassAsync(host);
        }
        finally
        {
            await host.StopAsync();
        }

        recordedAtSecondPass.Should().BeFalse("the first pass had a failed key, so it left no record");
        flaky.Calls("b").Should().Be(2);
        (await kit.Kit.ExistsAsync(kit.Kit.N("b"))).Should().BeTrue();
        (await kit.Db.StringGetAsync(passKey)).ToString().Should().EndWith("|0|" + (await kit.Db.StringGetAsync(passKey)).ToString().Split('|')[4]);
    }

    [Fact]
    public async Task A_pass_record_with_failed_keys_cannot_confirm_the_drain()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("k");
        var unit = (await kit.UnitIdsAsync()).Single();
        var passes = new MoverPasses(kit.Units, kit.Db, kit.Kit.Names, (_, _) => Task.FromResult<StateRecord?>(kit.State()));
        var passKey = kit.Kit.Names.UnitPass(kit.Kit.Group, unit, "fwd", MoverKit.MoveId);

        await kit.Db.StringSetAsync(passKey, $"{kit.Kit.Id}|{MoverKit.MoveId}|1|2|src");
        (await passes.FinishedAfterAckAsync(kit.Kit.Group, "fwd", Ct)).Should().BeFalse();
        await kit.Db.StringSetAsync(passKey, $"{kit.Kit.Id}|{MoverKit.MoveId}|1|0|src");
        (await passes.FinishedAfterAckAsync(kit.Kit.Group, "fwd", Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_scan_reads_the_configured_logical_database()
    {
        await using var kit = await MoverKit.CreateAsync(fixture, database: 3);
        var decoy = kit.Kit.L("decoy");
        await kit.Kit.Mux.GetDatabase(0).StringSetAsync(decoy, "x");
        try
        {
            await kit.SeedAsync("a", "b");

            await kit.RunOnePassAsync(kit.Host());

            (await kit.ProgressAsync()).Should().BeEquivalentTo(new Dictionary<string, long> { ["moved"] = 2 });
            (await kit.Kit.GetAsync(kit.Kit.N("a"))).Should().Be("v-a");
        }
        finally
        {
            await kit.Kit.Mux.GetDatabase(0).KeyDeleteAsync(decoy);
        }
    }

    [Fact]
    public async Task A_unit_waits_at_least_the_minimum_between_two_scan_calls()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(Enumerable.Range(0, 30).Select(i => $"k{i}").ToArray());
        ScanRecordingServer? scans = null;
        var host = kit.Host(Small with { MinScanInterval = TimeSpan.FromMilliseconds(60) }, units: new ProxyUnits(kit.Units, u => u with { Primary = ScanRecordingServer.Over(u.Primary, out scans) }));
        var started = kit.Kit.Clock.GetTimestamp();

        // Five scans are enough to show the spacing, whatever else the server holds.
        host.Start();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                await kit.Kit.Time.AdvanceNextTimerAsync(Ct);
            }

            await kit.Kit.Time.NextTimerAsync(Ct);
        }
        finally
        {
            await host.StopAsync();
        }

        var count = scans!.Cursors.Count;
        var elapsed = kit.Kit.Clock.GetElapsedTime(started);
        count.Should().BeGreaterThan(3);
        elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(60 * (count - 1) * 0.8));
    }

    [Fact]
    public async Task Passes_are_paced_by_the_life_of_a_key_so_a_second_host_does_not_start_another_pass()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("a", "b");
        await kit.RunOnePassAsync(kit.Host());
        var unit = (await kit.UnitIdsAsync()).Single();
        (await kit.Db.KeyTimeToLiveAsync(kit.Kit.Names.UnitPace(kit.Kit.Group, unit, "fwd", MoverKit.MoveId)))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(30));
        ScanRecordingServer? scans = null;
        var second = kit.Host(MoverKit.Quick, units: new ProxyUnits(kit.Units, u => u with { Primary = ScanRecordingServer.Over(u.Primary, out scans) }));

        second.Start();
        try
        {
            await kit.Kit.Time.NextTimerAsync(Ct);
        }
        finally
        {
            await second.StopAsync();
        }

        scans!.Cursors.Should().BeEmpty("a pass started less than PassPause ago");
    }

    [Fact]
    public async Task A_persistent_failure_is_alerted_once_in_the_period()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("a", "b", "c");
        var units = new ProxyUnits(kit.Units, u =>
        {
            var server = ScanRecordingServer.Over(u.Primary, out var recorder);
            recorder.Fail = true;
            return u with { Primary = server };
        });
        var broken = kit.Host(MoverKit.Quick, units: units);

        broken.Start();
        try
        {
            for (var poll = 0; poll < 15; poll++)
            {
                await kit.Kit.Time.AdvanceNextTimerAsync(Ct);
            }

            await kit.Kit.Time.NextTimerAsync(Ct);
        }
        finally
        {
            await broken.StopAsync();
        }

        kit.Kit.Plane.Alerts.Count(a => a.Code == "mover_unit_failed").Should().Be(1);
    }

    [Fact]
    public async Task A_managed_cache_without_a_target_is_alerted_once_and_its_keys_stay()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("a", "b", "c");
        var untargeted = kit.Host(MoverKit.Quick, wrap: _ => new Renamed(kit.Target));

        untargeted.Start();
        try
        {
            await kit.Kit.Time.NextTimerAsync(Ct);
        }
        finally
        {
            await untargeted.StopAsync();
        }

        kit.Kit.Plane.Alerts.Count(a => a.Code == "mover_no_target").Should().Be(1);
        (await kit.Kit.ExistsAsync(kit.Kit.N("a"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_writer_that_replaces_the_fence_between_the_capture_and_the_publish_is_never_overwritten_by_the_mover()
    {
        KeyMoveKit? held = null;
        await using var kit = await MoverKit.CreateAsync(fixture, schedule: new WriterAtPublish(async () => await held!.Client.SetAsync("k", new Item("new"), null, Ct)));
        held = kit.Kit;
        await kit.SeedAsync("k");

        var result = await kit.Target.MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, null, Ct);

        result.Moved.Should().BeFalse();
        result.SkipReason.Should().Be("duplicate", "the writer deleted L, so the second run finds nothing to promote");
        (await kit.Kit.GetAsync(kit.Kit.N("k"))).Should().Be("new");
        (await kit.Kit.ExistsAsync(kit.Kit.L("k"))).Should().BeFalse();
    }

    private sealed class Renamed(IMoveTarget inner) : IMoveTarget
    {
        public string CacheId => "a-cache-the-keys-do-not-belong-to";

        public bool NoOrigin => inner.NoOrigin;

        public ValueTask<DryRunProbe> ProbeAsync(string key, TimeSpan minTtl, bool decode, CancellationToken cancellationToken) => inner.ProbeAsync(key, minTtl, decode, cancellationToken);

        public ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, Action<long, long>? observeSourceTtl, CancellationToken cancellationToken) => inner.MoveAsync(key, minTtl, identity, ownership, observeSourceTtl, cancellationToken);
    }
}
