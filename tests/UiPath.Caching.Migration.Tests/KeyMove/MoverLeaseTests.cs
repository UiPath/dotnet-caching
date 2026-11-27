using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A takeover resumes from the cursor the previous lease holder saved.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoverLeaseTests(RedisFixture fixture)
{
    private static readonly MoverOptions Small = new()
    {
        ScanCount = 5,
        PollInterval = TimeSpan.FromMilliseconds(40),
        PassPause = TimeSpan.FromHours(1),
        MinScanInterval = TimeSpan.Zero,
        LeaseTtl = TimeSpan.FromSeconds(2),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_cursor_is_stored_after_each_batch_under_the_lease_and_a_finished_pass_leaves_none_and_releases_the_lease()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(Enumerable.Range(0, 40).Select(i => $"k{i}").ToArray());
        var gate = new GateTarget(kit.Target, blockAt: 12);
        var host = kit.Host(Small, _ => gate);
        var unit = (await kit.UnitIdsAsync()).Single();
        var names = kit.Kit.Names;
        host.Start();
        try
        {
            await gate.ReachedAsync;

            // Held in the middle of a later batch: the earlier batches have stored where the scan stands.
            var cursor = ScanCursor.Parse(await kit.Db.StringGetAsync(names.UnitCursor(kit.Kit.Group, unit, "fwd", MoverKit.MoveId)));
            cursor.Should().NotBeNull();
            cursor.Cursor.Should().NotBe("0");
            (await kit.Db.StringGetAsync(names.UnitLease(kit.Kit.Group, unit))).IsNull.Should().BeFalse();
            (await kit.Db.KeyTimeToLiveAsync(names.UnitLease(kit.Kit.Group, unit)))!.Value.Should().BeLessThanOrEqualTo(Small.LeaseTtl);
            gate.Release();
            await kit.WaitForPassAsync(host);
        }
        finally
        {
            gate.Release();
            await host.StopAsync();
        }

        (await kit.Db.KeyExistsAsync(names.UnitCursor(kit.Kit.Group, unit, "fwd", MoverKit.MoveId))).Should().BeFalse();
        (await kit.Db.KeyExistsAsync(names.UnitLease(kit.Kit.Group, unit))).Should().BeFalse("a stop releases the lease");
        (await kit.ProgressAsync())["moved"].Should().Be(40);
    }

    [Fact]
    public async Task A_holder_killed_in_the_middle_of_a_batch_is_replaced_and_the_unit_resumes_from_the_stored_cursor_with_every_key_called_exactly_once()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var keys = Enumerable.Range(0, 40).Select(i => $"k{i}").ToArray();
        await kit.SeedAsync(keys);
        var moves = new CountingTarget(kit.Target);
        var unit = (await kit.UnitIdsAsync()).Single();
        var names = kit.Kit.Names;
        var cursorKey = names.UnitCursor(kit.Kit.Group, unit, "fwd", MoverKit.MoveId);
        var (planeA, freezer) = kit.FreezablePlane();
        string? cursorAtCrash = null;
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var crash = new CrashTarget(moves, 15, async () =>
        {
            cursorAtCrash = await kit.Db.StringGetAsync(cursorKey);

            // The process is gone: nothing it sends reaches Redis again, so its lease stays held and its cursor stays where it was stored.
            freezer.Frozen = true;
            crashed.SetResult();
        });
        var holder = kit.Host(Small, _ => crash, plane: planeA);
        ScanRecordingServer? scans = null;
        var other = kit.Host(Small, _ => moves, units: new ProxyUnits(kit.Units, u => u with { Primary = ScanRecordingServer.Over(u.Primary, out scans) }));
        holder.Start();
        await crashed.Task.WaitAsync(Ct);
        cursorAtCrash.Should().NotBeNull("an earlier batch had stored its cursor before the holder died");
        var lease = names.UnitLease(kit.Kit.Group, unit);
        (await kit.Db.KeyExistsAsync(lease)).Should().BeTrue("a crash releases nothing");
        await holder.StopAsync();

        var remaining = await kit.Db.KeyTimeToLiveAsync(lease);
        if (remaining is { } ttl)
        {
            await Task.Delay(ttl + TimeSpan.FromMilliseconds(2), Ct);
        }

        (await kit.Db.KeyExistsAsync(lease)).Should().BeFalse("the lease lapses on the server");
        other.Start();
        try
        {
            await kit.WaitForPassAsync(other);
        }
        finally
        {
            await other.StopAsync();
        }

        foreach (var key in keys)
        {
            (await kit.Kit.GetAsync(kit.Kit.N(key))).Should().Be("v-" + key);
            (await kit.Kit.ExistsAsync(kit.Kit.L(key))).Should().BeFalse(key);
            moves.Calls(key).Should().Be(1, key);
            moves.Moved(key).Should().Be(1, key);
        }

        scans!.Cursors[0].Should().Be(ScanCursor.Parse(cursorAtCrash)!.Cursor, "the unit resumes from the stored cursor and does not scan again from zero");
        (await kit.ProgressAsync())["lease_takeovers"].Should().Be(1);
        (await kit.ProgressAsync()).Should().NotContainKey("skipped:duplicate");
    }

    // The holder runs on a connection of its own, which is closed in the middle of a batch as the connection of a killed process is.
    [Fact]
    public async Task A_holder_whose_own_connection_is_closed_in_the_middle_of_a_batch_is_replaced_and_every_key_moves_once()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var pass = new MoverPassCompletion();
        await using var kit = await MoverKit.CreateAsync(fixture, clock: clock, interceptDb: pass.Observe);
        var keys = Enumerable.Range(0, 40).Select(i => $"k{i}").ToArray();
        await kit.SeedAsync(keys);
        var moves = new CountingTarget(kit.Target);
        var unit = (await kit.UnitIdsAsync()).Single();
        var lease = kit.Kit.Names.UnitLease(kit.Kit.Group, unit);
        await using (var own = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString))
        {
            var data = kit.Kit.Plane.Data!;
            var db = own.GetDatabase();
            var planeA = new StubPlane(kit.Kit.Group, kit.Kit.Gate)
            {
                Data = data with { Db = db, Scripts = new MoveScripts(db, true, new Version(7, 0)), IsConnected = () => own.IsConnected },
                Rules = kit.Kit.Plane.Rules,
            };
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var crash = new CrashTarget(moves, 15, async () =>
            {
                await own.CloseAsync(allowCommandsToComplete: false);
                closed.SetResult();
            });
            var holder = kit.Host(Small, _ => crash, plane: planeA);

            holder.Start();
            try
            {
                await closed.Task.WaitAsync(TestContext.Current.CancellationToken);
                (await kit.Db.KeyExistsAsync(lease)).Should().BeTrue("the closed connection released nothing");
            }
            finally
            {
                await holder.StopAsync();
            }
        }

        // Redis owns the lease TTL, so delete the lease when the test clock expires it.
        clock.Advance(Small.LeaseTtl);
        await kit.Db.KeyDeleteAsync(lease);
        await pass.RunAsync(kit, kit.Host(Small, _ => moves), TestContext.Current.CancellationToken);

        foreach (var key in keys)
        {
            (await kit.Kit.GetAsync(kit.Kit.N(key))).Should().Be("v-" + key);
            (await kit.Kit.ExistsAsync(kit.Kit.L(key))).Should().BeFalse(key);
            moves.Moved(key).Should().Be(1, key);
        }

        (await kit.ProgressAsync())["lease_takeovers"].Should().Be(1);
    }

    [Fact]
    public async Task A_holder_that_is_paused_past_its_lease_promotes_nothing_when_it_resumes_after_another_runner_took_the_unit()
    {
        // The lease lifetime runs on the manual clock: the holder is stalled past it, and the unit is taken the way a lapsed lease is.
        var clock = new ObservedClock(DateTimeOffset.UnixEpoch.AddDays(1));
        await using var kit = await MoverKit.CreateAsync(fixture, clock: clock);
        var keys = Enumerable.Range(0, 40).Select(i => $"k{i}").ToArray();
        await kit.SeedAsync(keys);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderMoves = new CountingTarget(kit.Target)
        {
            Before = async (_, _) =>
            {
                if (paused.TrySetResult())
                {
                    await release.Task;
                }

                return null;
            },
        };
        var otherMoves = new CountingTarget(kit.Target);
        var whole = Small with { ScanCount = 500, LeaseTtl = TimeSpan.FromSeconds(30) };
        var holder = kit.Host(whole, _ => holderMoves);
        var other = kit.Host(whole, _ => otherMoves);
        var lease = kit.Kit.Names.UnitLease(kit.Kit.Group, (await kit.UnitIdsAsync()).Single());

        holder.Start();
        await paused.Task.WaitAsync(Ct);
        var before = holderMoves.Total;
        clock.Advance(whole.LeaseTtl + TimeSpan.FromSeconds(1));
        await kit.Db.KeyDeleteAsync(lease);
        other.Start();
        try
        {
            await kit.WaitForPassAsync(other);
            await other.StopAsync();

            release.SetResult();
            (await clock.NextTimerAsync(Ct)).Should().Be(whole.PollInterval);
        }
        finally
        {
            release.TrySetResult();
            await holder.StopAsync();
            await other.StopAsync();
        }

        holderMoves.Total.Should().BeLessThanOrEqualTo(before + 1, "the key in flight when it paused may finish, but a holder that finds its lease gone schedules no further key");
        otherMoves.Total.Should().BeGreaterThan(30);
    }

    [Fact]
    public async Task The_lease_is_renewed_inside_a_batch_that_outlasts_it_so_no_other_runner_takes_the_unit()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var keys = Enumerable.Range(0, 16).Select(i => $"k{i}").ToArray();
        await kit.SeedAsync(keys);
        var firstMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new CountingTarget(kit.Target)
        {
            Before = async (_, _) =>
            {
                if (firstMove.TrySetResult())
                {
                    await resume.Task.WaitAsync(Ct);
                }

                kit.Kit.Time.Advance(TimeSpan.FromMilliseconds(150));
                return null;
            },
        };
        var bystander = new CountingTarget(kit.Target);
        var whole = Small with { ScanCount = 500 };
        var holder = kit.Host(whole, _ => slow);
        var other = kit.Host(whole, _ => bystander);

        holder.Start();
        await firstMove.Task.WaitAsync(Ct);
        other.Start();
        try
        {
            (await kit.Kit.Time.NextTimerAsync(Ct)).Should().Be(whole.PollInterval);
            resume.SetResult();
            await kit.Kit.Time.NextTimerAsync(delay => delay == whole.PassPause, Ct);
            (await kit.PassedAsync()).Should().BeTrue();
        }
        finally
        {
            resume.TrySetResult();
            await holder.StopAsync();
            await other.StopAsync();
        }

        bystander.Total.Should().Be(0, "the batch took longer than the lease, which the holder kept renewing");
        keys.Should().OnlyContain(k => slow.Calls(k) == 1);
    }

    [Fact]
    public async Task A_cursor_write_is_refused_for_a_token_that_does_not_hold_the_lease_and_extends_the_lease_for_the_one_that_does()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var names = kit.Kit.Names;
        var (lease, cursor, pass) = (names.UnitLease(kit.Kit.Group, "u"), names.UnitCursor(kit.Kit.Group, "u", "fwd", 1), names.UnitPass(kit.Kit.Group, "u", "fwd", 1));
        await kit.Kit.Scripts.LeaseAsync(lease, "holder", 1000);

        (await kit.Kit.Scripts.UnitStepAsync(lease, cursor, pass, "intruder", 60_000, "c", "p", 60_000, 60_000)).Should().Be(MoveScripts.UnitLeaseLost);
        (await kit.Db.KeyExistsAsync(cursor)).Should().BeFalse();
        (await kit.Db.KeyExistsAsync(pass)).Should().BeFalse();
        (await kit.Kit.Scripts.UnitStepAsync(lease, cursor, pass, "holder", 60_000, "c", "p", 60_000, 60_000)).Should().Be(MoveScripts.UnitStepWritten);
        (await kit.Db.StringGetAsync(cursor)).ToString().Should().Be("c");
        (await kit.Db.KeyTimeToLiveAsync(lease))!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Two_hosts_share_a_unit_under_one_lease_and_every_key_is_moved_once()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var keys = Enumerable.Range(0, 30).Select(i => $"k{i}").ToArray();
        await kit.SeedAsync(keys);
        var moves = new CountingTarget(kit.Target);
        var one = kit.Host(Small, _ => moves);
        var two = kit.Host(Small, _ => moves);

        one.Start();
        two.Start();
        try
        {
            await kit.PollClock(one).NextTimerAsync(Ct);
            await kit.PollClock(two).NextTimerAsync(Ct);
            await kit.Kit.Time.NextTimerAsync(delay => delay == Small.PassPause, Ct);
            (await kit.PassedAsync()).Should().BeTrue();
            (one.RunningUnits.Count + two.RunningUnits.Count).Should().Be(2, "each host has a runner for the unit, one of which waits for the lease");
        }
        finally
        {
            await one.StopAsync();
            await two.StopAsync();
        }

        keys.Should().OnlyContain(k => moves.Calls(k) == 1 && moves.Moved(k) == 1);
        (await kit.ProgressAsync()).Should().BeEquivalentTo(new Dictionary<string, long> { ["moved"] = 30 }, "a second runner on the unit would have found the keys gone and counted duplicates");
    }

    [Fact]
    public async Task A_pass_counts_only_when_its_record_names_the_current_identity_and_move()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("k");
        await kit.RunOnePassAsync(kit.Host());
        var unit = (await kit.UnitIdsAsync()).Single();
        var passKey = kit.Kit.Names.UnitPass(kit.Kit.Group, unit, "fwd", MoverKit.MoveId);
        var ct = TestContext.Current.CancellationToken;
        MoverPasses At(StateRecord state) => new(kit.Units, kit.Db, kit.Kit.Names, (_, _) => Task.FromResult<StateRecord?>(state));

        (await At(kit.State()).FinishedAfterAckAsync(kit.Kit.Group, "fwd", ct)).Should().BeTrue();
        (await At(kit.State() with { Id = "newer" }).FinishedAfterAckAsync(kit.Kit.Group, "fwd", ct)).Should().BeFalse("the pass ran under an older identity");
        (await At(kit.State() with { Id = "newer", AllAck = "newer" }).FinishedAfterAckAsync(kit.Kit.Group, "fwd", ct)).Should().BeFalse();
        (await At(kit.State() with { MoveId = MoverKit.MoveId + 1 }).FinishedAfterAckAsync(kit.Kit.Group, "fwd", ct)).Should().BeFalse("another move id has no record");

        await kit.Db.StringSetAsync(passKey, $"older|{MoverKit.MoveId}|1");
        (await At(kit.State()).FinishedAfterAckAsync(kit.Kit.Group, "fwd", ct)).Should().BeFalse("a record under an older identity");
        await kit.Db.StringSetAsync(passKey, $"{kit.Kit.Id}|{MoverKit.MoveId + 1}|1");
        (await At(kit.State()).FinishedAfterAckAsync(kit.Kit.Group, "fwd", ct)).Should().BeFalse("a record under another move id");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_group_with_a_hash_cache_cannot_confirm_the_drain_from_mover_passes(bool hashCache, bool expected)
    {
        await using var kit = await MoverKit.CreateAsync(fixture, hashCache: hashCache);
        await kit.SeedAsync("k");
        await kit.RunOnePassAsync(kit.Host());
        var alerts = new List<KeyMoveAlert>();
        var passes = new MoverPasses(kit.Units, kit.Db, kit.Kit.Names, (_, _) => Task.FromResult<StateRecord?>(kit.State()), kit.Space.HasHashCache, alerts.Add);

        (await passes.FinishedAfterAckAsync(kit.Kit.Group, "fwd", TestContext.Current.CancellationToken)).Should().Be(expected, "every pass finished under the current identity");

        IEnumerable<string> expectedAlerts = hashCache ? [MoverPasses.NoHashPromotion] : [];
        alerts.Select(a => a.Code).Should().Equal(expectedAlerts);
    }

    [Theory]
    [InlineData("S2", "fwd", true, "fwd", false)]
    [InlineData("S3", "fwd", false, "fwd", false)]
    [InlineData("S3", "fwd", true, "fwd", true)]
    [InlineData("S3", "rev", true, "fwd", false)]
    [InlineData("S3", "rev", true, "rev", true)]
    public async Task A_matching_pass_record_requires_an_acknowledged_move_identity_and_direction(string state, string direction, bool acked, string askedDirection, bool expected)
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("k");
        var record = kit.State(state, acked, direction);
        var passes = new MoverPasses(kit.Units, kit.Db, kit.Kit.Names, (_, _) => Task.FromResult<StateRecord?>(record));
        var unit = (await kit.UnitIdsAsync()).Single();

        (await passes.FinishedAfterAckAsync(kit.Kit.Group, askedDirection, TestContext.Current.CancellationToken)).Should().BeFalse("no pass has finished yet");
        await kit.Db.StringSetAsync(kit.Kit.Names.UnitPass(kit.Kit.Group, unit, askedDirection, MoverKit.MoveId), PassRecord.Text(record.Id, MoverKit.MoveId, 1, 0, unit));
        (await passes.FinishedAfterAckAsync(kit.Kit.Group, askedDirection, TestContext.Current.CancellationToken)).Should().Be(expected);
    }

    [Fact]
    public async Task The_units_are_named_by_their_primary_and_a_replica_is_the_scan_server_when_there_is_one()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 to run.");
        var replica = Environment.GetEnvironmentVariable("CLAIM_REPLICA_CONNECTION_STRING");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(replica), "Set CLAIM_REPLICA_CONNECTION_STRING to run.");
        await using var both = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},{replica},allowAdmin=true");

        var units = await new MultiplexerScanUnits(() => both).ListAsync(TestContext.Current.CancellationToken);

        var unit = units.Should().ContainSingle().Subject;
        unit.Id.Should().Be(fixture.ConnectionString);
        unit.Primary.IsReplica.Should().BeFalse();
        unit.ScanServer.IsReplica.Should().BeTrue();
        unit.Replica.Should().NotBeNull();
    }

    [Fact]
    public async Task On_a_cluster_each_primary_is_a_unit_named_by_its_node_id_and_every_unit_finishes_a_pass()
    {
        await using var kit = await MoverKit.CreateAsync(fixture, cluster: true);
        var keys = Enumerable.Range(0, 30).Select(i => $"k{i}").ToArray();
        await kit.SeedAsync(keys);

        await kit.RunOnePassAsync(kit.Host());

        var units = await kit.UnitIdsAsync();
        units.Should().HaveCountGreaterThan(1).And.OnlyContain(id => id.Length == 40);
        foreach (var key in keys)
        {
            (await kit.Kit.GetAsync(kit.Kit.N(key))).Should().Be("v-" + key);
            (await kit.Kit.ExistsAsync(kit.Kit.L(key))).Should().BeFalse(key);
        }

        (await kit.ProgressAsync())["moved"].Should().Be(30);
    }

    // Dies on its n-th key: what runs there is not run, as with a process that is killed.
    private sealed class CrashTarget(IMoveTarget inner, int crashAt, Func<Task> crash) : IMoveTarget
    {
        private int _seen;

        public string CacheId => inner.CacheId;

        public bool NoOrigin => inner.NoOrigin;

        public ValueTask<DryRunProbe> ProbeAsync(string key, TimeSpan minTtl, bool decode, CancellationToken cancellationToken) => inner.ProbeAsync(key, minTtl, decode, cancellationToken);

        public async ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, Action<long, long>? observeSourceTtl, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _seen) == crashAt)
            {
                await crash();
                throw new OperationCanceledException("killed");
            }

            return await inner.MoveAsync(key, minTtl, identity, ownership, observeSourceTtl, cancellationToken);
        }
    }

    // Holds the n-th key until released, so a test can look at the server in the middle of a pass.
    private sealed class GateTarget(IMoveTarget inner, int blockAt) : IMoveTarget
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;

        public Task ReachedAsync => _reached.Task;

        public string CacheId => inner.CacheId;

        public bool NoOrigin => inner.NoOrigin;

        public ValueTask<DryRunProbe> ProbeAsync(string key, TimeSpan minTtl, bool decode, CancellationToken cancellationToken) => inner.ProbeAsync(key, minTtl, decode, cancellationToken);

        public void Release() => _released.TrySetResult();

        public async ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, Action<long, long>? observeSourceTtl, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _seen) == blockAt)
            {
                _reached.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }

            return await inner.MoveAsync(key, minTtl, identity, ownership, observeSourceTtl, cancellationToken);
        }
    }
}
