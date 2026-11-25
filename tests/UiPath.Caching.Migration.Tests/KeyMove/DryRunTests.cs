using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The dry run on the real scripts: what it reports for each class, and that it writes nothing.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class DryRunTests(RedisFixture fixture)
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_replica_disconnect_or_timeout_restarts_on_the_primary_without_keeping_partial_counts(bool timeout)
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await SeedEveryClassAsync(kit);
        var expected = await Runner(kit).RunAsync(Ct);
        var replicas = new List<ScanRecordingServer>();
        var primaries = new List<ScanRecordingServer>();
        var rate = new SlowRate(0, TimeSpan.Zero);
        var units = new ProxyUnits(kit.Units, u =>
        {
            var replica = ScanRecordingServer.Over(u.Primary, out var failing);
            failing.FailAfter = 1;
            failing.DisconnectOnFailure = !timeout;
            failing.ScanFailure = timeout ? new RedisTimeoutException(CommandFlags.None, "The replica timed out.", CommandStatus.Sent) : null;
            failing.ScanReply = _ => Page("42", kit.Kit.L("ok"));
            replicas.Add(failing);
            var primary = ScanRecordingServer.Over(u.Primary, out var recorded);
            primaries.Add(recorded);
            return u with { Primary = primary, Replica = replica };
        });

        var report = await Runner(kit, units: units, rate: rate).RunAsync(Ct);

        replicas.Should().ContainSingle().Which.Cursors.Should().Equal("0", "42");
        primaries.Should().ContainSingle().Which.Cursors.Should().StartWith("0");
        rate.Reserved.Should().HaveCount(9, "the replica probes one key before the primary probes all eight");
        report.Scanned.Should().Be(expected.Scanned);
        Counts(report, kit.Kit.Cid).Should().BeEquivalentTo(Counts(expected, kit.Kit.Cid));
    }

    [Fact]
    public async Task A_primary_scan_that_fails_fails_the_run_without_a_retry()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await SeedEveryClassAsync(kit);
        var primaries = new List<ScanRecordingServer>();
        var units = new ProxyUnits(kit.Units, u =>
        {
            var primary = ScanRecordingServer.Over(u.Primary, out var failing);
            failing.Fail = true;
            primaries.Add(failing);
            return u with { Primary = primary, Replica = null };
        });

        var run = async () => await Runner(kit, units: units).RunAsync(Ct);

        await run.Should().ThrowAsync<RedisException>();
        primaries.Should().ContainSingle().Which.Cursors.Should().ContainSingle("a unit with no replica is not tried twice");
    }

    [Fact]
    public async Task A_unit_whose_replica_and_primary_both_fail_fails_the_run()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await SeedEveryClassAsync(kit);
        var units = new ProxyUnits(kit.Units, u =>
        {
            var replica = ScanRecordingServer.Over(u.Primary, out var failingReplica);
            failingReplica.Fail = true;
            var primary = ScanRecordingServer.Over(u.Primary, out var failingPrimary);
            failingPrimary.Fail = true;
            return u with { Primary = primary, Replica = replica };
        });

        var run = async () => await Runner(kit, units: units).RunAsync(Ct);

        await run.Should().ThrowAsync<RedisException>();
    }

    [Fact]
    public async Task Every_class_of_a_cache_is_counted_exactly()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await SeedEveryClassAsync(kit);

        var report = await Runner(kit).RunAsync(Ct);

        Counts(report, kit.Kit.Cid).Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [DryRunClasses.Movable] = 2,
            [DryRunClasses.ShortTtl] = 1,
            [DryRunClasses.TooLarge] = 1,
            [DryRunClasses.Expired] = 1,
            [DryRunClasses.Unreadable] = 1,
            [DryRunClasses.Unmappable] = 1,
            [DryRunClasses.Duplicate] = 1,
            [DryRunClasses.NullNotCached] = 1,
        });
        report.Caches.Should().ContainSingle();
        report.Scanned.Should().Be(9);
        report.Units.Should().Be((await kit.UnitIdsAsync()).Length);
        report.Summary().Should().Be("movable=2,duplicate=1,expired=1,null_not_cached=1,short_ttl=1,too_large=1,unmappable=1,unreadable=1");
    }

    [Fact]
    public async Task Keys_no_cache_claims_or_two_claim_or_no_target_serves_are_counted_under_their_own_ids()
    {
        await using var kit = await MoverKit.CreateAsync(fixture, twoOwners: true);
        await kit.SeedAsync("a1", "a2", "s1", "b1", "ok");

        var report = await Runner(kit).RunAsync(Ct);

        Counts(report, kit.Kit.Cid).Should().BeEquivalentTo(new Dictionary<string, long> { [DryRunClasses.Movable] = 2 });
        Counts(report, kit.Kit.Cid + "+" + kit.Kit.Cid + "b").Should().BeEquivalentTo(new Dictionary<string, long> { [DryRunClasses.Ambiguous] = 1 });
        Counts(report, kit.Kit.Cid + "b").Should().BeEquivalentTo(new Dictionary<string, long> { [DryRunClasses.NoTarget] = 1 });
        Counts(report, string.Empty).Should().BeEquivalentTo(new Dictionary<string, long> { [DryRunClasses.ExpectedUnmapped] = 1 });
        report.Scanned.Should().Be(5);
        var unowned = report.Fields().Single(f => f.Key.EndsWith(":" + DryRunClasses.ExpectedUnmapped, StringComparison.Ordinal));
        var cacheId = unowned.Key.Split(':')[0];
        var validate = () => MoveKeyNames.ValidateName(cacheId, nameof(cacheId));
        validate.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task A_key_matched_by_two_scan_patterns_is_counted_once()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("a1", "b1");
        var cache = CacheOf(kit);
        var overlapping = cache with { Name = "overlap", InPlacePattern = kit.Kit.L("a[12]"), OwnsInPlace = _ => false, OwnsNative = _ => false };
        var space = new RegisteredKeySpace([cache, overlapping], kit.Kit.N(string.Empty));

        var report = await Runner(kit, space: space).RunAsync(Ct);

        report.Scanned.Should().Be(2);
        report.Caches[kit.Kit.Cid].Movable.Should().Be(2);
    }

    [Fact]
    public async Task A_hash_cache_key_is_counted_without_probing_its_value()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var cache = CacheOf(kit) with { Hash = true, Kind = KeyMoveKind.Hash };
        var space = new RegisteredKeySpace([cache], kit.Kit.N(string.Empty));
        await kit.Db.HashSetAsync(kit.Kit.L("key"), "field", "value");
        var rate = new SlowRate(0, TimeSpan.Zero);

        var report = await Runner(kit, space: space, rate: rate).RunAsync(Ct);

        report.Scanned.Should().Be(1);
        Counts(report, kit.Kit.Cid).Should().ContainSingle().Which.Should().Be(KeyValuePair.Create("hash", 1L));
        rate.Reserved.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_in_Redis_changes()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await SeedEveryClassAsync(kit);
        var before = await DumpAsync(kit);

        await Runner(kit).RunAsync(Ct);

        (await DumpAsync(kit)).Should().BeEquivalentTo(before);
        (await kit.Db.KeyExistsAsync(kit.Kit.Names.Progress(kit.Kit.Group))).Should().BeFalse();
    }

    [Fact]
    public async Task Past_the_decode_cap_one_key_in_every_few_is_decoded_and_the_counts_stay_exact()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(Enumerable.Range(1, 8).Select(i => "k" + i).ToArray());

        var report = await Runner(kit, options: new DryRunOptions { DecodeCap = 2, SampleEvery = 3 }).RunAsync(Ct);

        var cache = report.Caches[kit.Kit.Cid];
        cache.Decoded.Should().Be(4);
        cache.Sampled.Should().BeTrue();
        cache.Movable.Should().Be(8);
        cache.EncodedKeys.Should().Be(4);
    }

    [Fact]
    public async Task A_cache_decoded_in_full_is_not_sampled()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("k1", "k2", "k3");

        var report = await Runner(kit).RunAsync(Ct);

        report.Caches[kit.Kit.Cid].Sampled.Should().BeFalse();
        report.Caches[kit.Kit.Cid].Decoded.Should().Be(3);
    }

    [Fact]
    public async Task The_sizes_fall_in_five_buckets_of_the_value_on_the_in_place_side()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var sizes = new[] { 10, 2_000, 20_000, 300_000, 2_000_000 };
        await kit.SeedAsync(sizes.ToDictionary(n => "k" + n, n => (new string('x', n), (TimeSpan?)null)));

        var report = await Runner(kit).RunAsync(Ct);

        var cache = report.Caches[kit.Kit.Cid];
        cache.Sizes.Should().Equal(1, 1, 1, 1, 1);
        cache.SourceBytes.Should().Be(sizes.Sum());
    }

    [Fact]
    public async Task The_estimate_is_the_mean_encoded_size_of_the_sample_times_the_movable_keys()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(Enumerable.Range(1, 4).ToDictionary(i => "k" + i, i => (new string('x', 10), (TimeSpan?)null)));

        var report = await Runner(kit, options: new DryRunOptions { DecodeCap = 1, SampleEvery = 100 }).RunAsync(Ct);

        report.Caches[kit.Kit.Cid].Decoded.Should().Be(1);
        report.EstimatedNativeBytes.Should().Be(40);
    }

    [Fact]
    public async Task The_keys_per_second_estimate_includes_probes_that_cannot_move()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(new Dictionary<string, (string, TimeSpan?)> { ["short"] = ("v", TimeSpan.FromMinutes(1)) });

        var report = await Runner(kit).RunAsync(Ct);

        report.EstimatedNativeBytes.Should().Be(0);
        report.EstimatedDuration.Should().Be(TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public async Task With_no_encoded_sample_the_estimate_uses_only_movable_source_bytes()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(new Dictionary<string, (string, TimeSpan?)>
        {
            ["movable"] = ("movable", null),
            ["short"] = ("short", TimeSpan.FromMinutes(1)),
        });

        var report = await Runner(kit, options: new DryRunOptions { DecodeCap = 0, SampleEvery = 3 }).RunAsync(Ct);

        report.Caches[kit.Kit.Cid].EncodedKeys.Should().Be(0);
        report.EstimatedNativeBytes.Should().Be(7);
    }

    [Theory]
    [InlineData(-1, 1, nameof(DryRunOptions.DecodeCap))]
    [InlineData(0, 0, nameof(DryRunOptions.SampleEvery))]
    public async Task Invalid_sampling_options_are_rejected_before_the_scan(int cap, int every, string option)
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var construct = () => Runner(kit, options: new DryRunOptions { DecodeCap = cap, SampleEvery = every });

        construct.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("options").WithMessage($"*{option}*");
    }

    [Fact]
    public async Task Only_a_value_that_was_read_and_decoded_counts_and_an_empty_value_has_a_bucket()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.Db.StringSetAsync(kit.Kit.L("empty"), "");
        var units = new ProxyUnits(kit.Units, u =>
        {
            var server = ScanRecordingServer.Over(u.Primary, out var recorder);
            recorder.ScanReply = _ => Page("0", kit.Kit.L("empty"), kit.Kit.L("gone"));
            return u with { Primary = server, Replica = null };
        });

        var cache = (await Runner(kit, units: units).RunAsync(Ct)).Caches[kit.Kit.Cid];

        cache.Counts.Should().ContainKey(DryRunClasses.Absent).WhoseValue.Should().Be(1);
        cache.Decoded.Should().Be(1);
        cache.Sizes.Should().Equal(1, 0, 0, 0, 0);
    }

    [Theory]
    [InlineData(2, 1000, 2)]
    [InlineData(1000, 10, 4)]
    public async Task The_duration_is_the_slower_of_the_keys_and_the_bytes_at_the_starting_rate_shared_by_the_units(double keysPerSecond, double bytesPerSecond, double seconds)
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(Enumerable.Range(1, 4).ToDictionary(i => "k" + i, i => (new string('x', 10), (TimeSpan?)null)));
        var rates = new KeyMoveMoverOptions { StartKeysPerSecond = keysPerSecond, StartBytesPerSecond = bytesPerSecond };

        var report = await Runner(kit, rates: rates).RunAsync(Ct);

        report.EstimatedDuration.Should().Be(TimeSpan.FromSeconds(seconds / Math.Max(report.Units, 1)));
    }

    [Fact]
    public async Task A_rate_that_pauses_and_waits_delays_the_run_and_the_report_is_complete()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var kit = await MoverKit.CreateAsync(fixture, clock: clock);
        await kit.SeedAsync("k1", "k2", "k3");
        var unit = (await kit.Units.ListAsync(Ct)).Should().ContainSingle().Subject;
        var server = ScanRecordingServer.Over(unit.Primary, out var recorder);
        var units = new ProxyUnits(kit.Units, u => u with { Primary = server, Replica = null });
        const int pauses = 5;
        var rate = new SlowRate(pauses, wait: TimeSpan.FromMilliseconds(50));
        var run = Runner(kit, rate: rate, units: units).RunAsync(Ct);
        for (var i = 0; i < pauses + 3; i++)
        {
            var delay = await clock.NextTimerAsync(Ct);
            delay.Should().Be(i < pauses ? MoverKit.Quick.PollInterval : TimeSpan.FromMilliseconds(50));
            run.IsCompleted.Should().BeFalse();
            clock.Advance(delay);
        }
        var report = await run;

        clock.GetTimestamp().Should().Be((MoverKit.Quick.PollInterval * pauses + TimeSpan.FromMilliseconds(150)).Ticks);
        report.Caches[kit.Kit.Cid].Movable.Should().Be(3);
        rate.PauseCalls.Should().Be(pauses + recorder.Cursors.Count + 3);
        rate.Reserved.Should().Equal(4L, 4L, 4L);
    }

    [Fact]
    public async Task A_paused_rate_holds_the_first_scan_and_pages_use_the_configured_interval()
    {
        var clock = new ManualTimeProvider(TimeProvider.System.GetUtcNow());
        await using var kit = await MoverKit.CreateAsync(fixture, clock: clock);
        var unit = (await kit.Units.ListAsync(Ct)).Should().ContainSingle().Subject;
        var server = ScanRecordingServer.Over(unit.Primary, out var recorder);
        recorder.ScanReply = cursor => Page(cursor == "0" ? "42" : "0");
        var units = new ProxyUnits(kit.Units, u => u with { Primary = server, Replica = null });
        var rate = new SlowRate(1, TimeSpan.Zero);
        var interval = TimeSpan.FromSeconds(1);
        var options = MoverKit.Quick with { MinScanInterval = interval };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = Runner(kit, rate: rate, units: units, moverOptions: options).RunAsync(stop.Token);
        try
        {
            await MoverKit.WaitAsync(() => Task.FromResult(clock.HasTimers || run.IsCompleted));
            recorder.Cursors.Should().BeEmpty();
            clock.Advance(options.PollInterval);

            await MoverKit.WaitAsync(() => Task.FromResult(clock.HasTimers || run.IsCompleted));
            recorder.Cursors.Should().Equal("0");
            clock.Advance(interval - TimeSpan.FromMilliseconds(1));
            recorder.Cursors.Should().Equal("0");
            clock.Advance(TimeSpan.FromMilliseconds(1));

            (await run.WaitAsync(TimeSpan.FromSeconds(3), TimeProvider.System, Ct)).Scanned.Should().Be(0);
            recorder.Cursors.Should().Equal("0", "42");
        }
        finally
        {
            await stop.CancelAsync();
        }
    }

    [Fact]
    public async Task A_dry_run_does_not_replace_the_movers_pending_reservation_for_the_unit()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("key");
        var unit = (await kit.UnitIdsAsync()).Should().ContainSingle().Subject;
        var rateClock = new ManualTimeProvider(kit.Kit.Time.GetUtcNow());
        var rate = new AimdMoveRate(new MoveRateTests.FakeHealth(), null, new StaticOptionsMonitor<KeyMoveMoverOptions>(new KeyMoveMoverOptions()), rateClock);
        var pending = rate.Reserve(unit, 50, 0);

        var run = Runner(kit, rate: rate).RunAsync(Ct);
        var reservation = kit.Kit.Time.NextTimerAsync(Ct).AsTask();
        (await Task.WhenAny(run, reservation)).Should().BeSameAs(reservation);
        kit.Kit.Time.Advance(await reservation);
        await run;

        rate.Remaining(unit).Should().Be(pending);
        rate.Remaining("dry:" + unit).Should().Be(TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public async Task A_run_uses_the_data_view_whose_connection_it_checked()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var plane = kit.Kit.Plane;
        plane.Data = plane.Held! with
        {
            IsConnected = () =>
            {
                plane.Data = null;
                return true;
            },
        };

        var report = await Runner(kit).RunAsync(Ct);

        report.Scanned.Should().Be(0);
    }

    [Fact]
    public async Task A_run_waits_for_the_data_view_to_connect()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("k1");
        kit.Kit.Connected = false;

        var run = Runner(kit).RunAsync(Ct);
        var poll = kit.Kit.Time.NextTimerAsync(Ct).AsTask();
        (await Task.WhenAny(run, poll)).Should().BeSameAs(poll);
        run.IsCompleted.Should().BeFalse();
        kit.Kit.Connected = true;
        kit.Kit.Time.Advance(await poll);

        (await run).Caches[kit.Kit.Cid].Movable.Should().Be(1);
    }

    [Theory]
    [InlineData("k", "vv", 0, DryRunClasses.Movable, true)]
    [InlineData("k", "vv", 0, DryRunClasses.Movable, false)]
    [InlineData("k", "vv", 1, DryRunClasses.ShortTtl, true)]
    [InlineData("k", "vv", 1, DryRunClasses.ShortTtl, false)]
    [InlineData("k", "exp:1", 0, DryRunClasses.Expired, true)]
    [InlineData("k", "big:1", 0, DryRunClasses.TooLarge, true)]
    [InlineData("k", "junk:1", 0, DryRunClasses.Unreadable, true)]
    [InlineData("k", "null:1", 0, DryRunClasses.NullNotCached, true)]
    [InlineData("k", "junk:1", 0, DryRunClasses.Movable, false)]
    public async Task A_probe_classifies_a_key_and_measures_it_only_when_it_decodes(string key, string value, int ttlMinutes, string expected, bool decode)
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(new Dictionary<string, (string, TimeSpan?)> { [key] = (value, ttlMinutes == 0 ? null : TimeSpan.FromMinutes(ttlMinutes)) });

        var probe = await kit.Target.ProbeAsync(key, IGroupKeySpace.DefaultMoveMinTtl, decode, Ct);

        probe.Class.Should().Be(expected);
        probe.SourceBytes.Should().Be(value.Length);
        probe.EncodedBytes.Should().Be(decode && expected == DryRunClasses.Movable ? value.Length : null);
    }

    [Fact]
    public async Task A_probe_of_a_key_the_cache_does_not_map_is_unmappable()
    {
        await using var kit = await MoverKit.CreateAsync(
            fixture,
            source: k => new InPlaceMoveSide<Item>(k.Kit.Plane, k.Kit.Group, k.Kit.L, ItemCodec.Instance, key => !key.StartsWith("weird:", StringComparison.Ordinal), k.Kit.Clock));
        await kit.Db.StringSetAsync(kit.Kit.L("weird:1"), "vv");

        var probe = await kit.Target.ProbeAsync("weird:1", TimeSpan.Zero, true, Ct);

        probe.Should().Be(new DryRunProbe(DryRunClasses.Unmappable, 2, null, Decoded: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_probe_of_a_key_N_holds_is_a_duplicate(bool decode)
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.Db.StringSetAsync(kit.Kit.L("k"), "vv");
        await kit.Db.StringSetAsync(kit.Kit.N("k"), "n");

        var probe = await kit.Target.ProbeAsync("k", TimeSpan.Zero, decode, Ct);

        probe.Should().Be(new DryRunProbe(DryRunClasses.Duplicate, 2, null, decode));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_short_lived_source_is_classified_before_a_duplicate_destination(bool decode)
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.Db.StringSetAsync(kit.Kit.L("key"), "value", TimeSpan.FromMinutes(1));
        await kit.Db.StringSetAsync(kit.Kit.N("key"), "native");

        var probe = await kit.Target.ProbeAsync("key", Hour, decode, Ct);

        probe.Class.Should().Be(DryRunClasses.ShortTtl);
    }

    [Fact]
    public async Task An_oversized_source_is_too_large_even_without_decoding()
    {
        await using var kit = await MoverKit.CreateAsync(fixture, moveOptions: new KeyMoveOptions { MaxValueBytes = 3 });
        await kit.Db.StringSetAsync(kit.Kit.L("key"), "four");

        var probe = await kit.Target.ProbeAsync("key", TimeSpan.Zero, decode: false, Ct);

        probe.Class.Should().Be(DryRunClasses.TooLarge);
    }

    [Fact]
    public async Task A_probe_of_a_key_L_does_not_hold_is_absent()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);

        var probe = await kit.Target.ProbeAsync("none", TimeSpan.Zero, true, Ct);

        probe.Should().Be(new DryRunProbe(DryRunClasses.Absent, null, null));
    }

    [Fact]
    public async Task A_probe_of_a_key_that_is_not_a_string_is_unreadable()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.Db.ListRightPushAsync(kit.Kit.L("k"), "x");

        var probe = await kit.Target.ProbeAsync("k", TimeSpan.Zero, true, Ct);

        probe.Class.Should().Be(DryRunClasses.Unreadable);
    }

    [Fact]
    public async Task A_probe_with_no_connection_is_unavailable()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.Db.StringSetAsync(kit.Kit.L("k"), "vv");
        kit.Kit.Connected = false;

        var probe = await kit.Target.ProbeAsync("k", TimeSpan.Zero, true, Ct);

        probe.Class.Should().Be(DryRunClasses.Unavailable);
    }

    [Fact]
    public async Task A_probe_of_a_NoOrigin_cache_ignores_the_minimum_life()
    {
        await using var kit = await MoverKit.CreateAsync(fixture, noOrigin: true);
        await kit.Db.StringSetAsync(kit.Kit.L("k"), "vv", TimeSpan.FromMinutes(1));

        var probe = await kit.Target.ProbeAsync("k", Hour, true, Ct);

        probe.Class.Should().Be(DryRunClasses.Movable);
    }

    private static RedisResult Page(string cursor, params string[] keys) =>
        RedisResult.Create([RedisResult.Create((RedisValue)cursor), RedisResult.Create(keys.Select(key => (RedisValue)key).ToArray())]);

    private static KeyMoveCache CacheOf(MoverKit kit)
    {
        var legacy = kit.Kit.L(string.Empty);
        var native = kit.Kit.N(string.Empty);
        return new KeyMoveCache(
            kit.Kit.Cid,
            kit.Kit.Group,
            false,
            false,
            false,
            null,
            legacy + "*",
            kit.Kit.L,
            key => key.StartsWith(legacy, StringComparison.Ordinal) ? key[legacy.Length..] : null,
            native + "*",
            kit.Kit.N,
            key => key.StartsWith(native, StringComparison.Ordinal) ? key[native.Length..] : null);
    }

    private static Dictionary<string, long> Counts(DryRunReport report, string cacheId) => report.Caches[cacheId].Counts.ToDictionary(c => c.Key, c => c.Value);

    private static async Task SeedEveryClassAsync(MoverKit kit)
    {
        await kit.SeedAsync(new Dictionary<string, (string, TimeSpan?)>
        {
            ["ok"] = ("v", null),
            ["ok2"] = ("v2", Hour),
            ["short"] = ("v", TimeSpan.FromMinutes(1)),
            ["big:1"] = ("big:1", null),
            ["exp:1"] = ("exp:1", null),
            ["junk:1"] = ("junk:1", null),
            ["weird:1"] = ("v", null),
            ["dup"] = ("v", null),
            ["null:1"] = ("null:1", null),
        });
        await kit.Db.StringSetAsync(kit.Kit.N("dup"), "n-value");
    }

    // Every key of the test's namespace with its dump and whether it expires.
    private static async Task<Dictionary<string, (string Dump, bool Expires)>> DumpAsync(MoverKit kit)
    {
        var dump = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        foreach (var key in kit.Kit.Mux.GetServers()[0].Keys(pattern: $"*{kit.Kit.Unique}*"))
        {
            var bytes = await kit.Db.KeyDumpAsync(key);
            dump[key.ToString()] = (Convert.ToBase64String(bytes ?? []), await kit.Db.KeyTimeToLiveAsync(key) is not null);
        }

        return dump;
    }

    private static DryRunner Runner(MoverKit kit, IMoveRate? rate = null, DryRunOptions? options = null, KeyMoveMoverOptions? rates = null, IScanUnits? units = null, MoverOptions? moverOptions = null, IGroupKeySpace? space = null) =>
        new(
            kit.Kit.Group,
            kit.Kit.Plane,
            space ?? kit.Space,
            new Dictionary<string, IMoveTarget> { [kit.Target.CacheId] = kit.Target },
            units ?? kit.Units,
            string.Empty,
            rate ?? NoLimit.Instance,
            kit.Kit.Clock,
            moverOptions ?? MoverKit.Quick,
            options,
            rates);

    // Pauses for the first calls and charges a wait for every key, and keeps the bytes it was asked to reserve.
    private sealed class SlowRate(int pauses, TimeSpan wait) : IMoveRate
    {
        private int _pauseCalls;

        public int PauseCalls => _pauseCalls;

        public List<long> Reserved { get; } = [];

        public ValueTask<string?> PauseReasonAsync(string unit, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(Interlocked.Increment(ref _pauseCalls) <= pauses ? "red" : null);

        public TimeSpan Reserve(string unit, int keys, long bytes)
        {
            Reserved.Add(bytes);
            return wait;
        }

        public void Owe(string unit, long bytes)
        {
        }

        public long TakeDebt(string unit) => 0;

        public void Observed(string unit, MoveResult result, TimeSpan elapsed)
        {
        }
    }
}
