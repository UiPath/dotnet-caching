using System.Collections.Concurrent;
using System.Net;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <summary>Moves a routed cache through Redis under concurrent application traffic.</summary>
[Collection("RedisIntegration")]
public sealed class KeyMoveLifecycleTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_live_cache_reaches_closed_without_losing_data() => await MoveToClosedAsync(cluster: false);

    [Fact]
    public async Task A_live_cache_moves_across_the_three_cluster_primaries() => await MoveToClosedAsync(cluster: true);

    [Fact]
    public async Task Preparing_a_move_completes_inventory_after_a_topology_retry()
    {
        await using var host = await MovingCacheHost.CreateAsync(fixture);
        await host.SeedAsync("retry", [1]);
        var topology = new SettlingInventoryTopology(host.Topology.ReadLayoutAsync);

        await host.PrepareMoveAsync(topology);

        topology.Reads.Should().Be(3);
        var stamp = await host.Db.HashGetAsync(host.Names.State(MovingCacheHost.Group), StateFields.DryRun).WaitAsync(Ct);
        (DryRunStamp.TryParse(stamp.ToString())?.EstimatedBytes).Should().Be(1024);
    }

    [Fact]
    public async Task The_router_step_completes_drain_inventory_after_a_topology_retry()
    {
        SettlingInventoryTopology? topology = null;
        await using var host = await MovingCacheHost.CreateAsync(
            fixture, wrapInventoryTopology: inner => topology = new SettlingInventoryTopology(inner.ReadLayoutAsync));

        await EnterMovingAsync(host);

        Assert.NotNull(topology);
        topology.Reads.Should().Be(3);
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task Moving_writes_refresh_UTC_without_advancing_fake_timers(bool expires, int offsetMinutes)
    {
        await using var host = await MovingCacheHost.CreateAsync(fixture);
        await EnterMovingAsync(host);
        var deadline = expires ? await KeyMoveKit.ServerTimeAsync(host.Db) + TimeSpan.FromMinutes(5) : (DateTimeOffset?)null;
        host.Clock.UtcOffset += TimeSpan.FromMinutes(offsetMinutes);
        var timestamp = host.Clock.GetTimestamp();
        var utc = host.Clock.GetUtcNow();

        Assert.True(await host.SetAsync("clock-drift", [1], deadline, Ct));

        host.Clock.GetTimestamp().Should().Be(timestamp);
        host.Clock.GetUtcNow().Should().BeOnOrAfter(utc);
        await host.AssertValueAsync("clock-drift", [1]);
        var ttl = await host.Db.KeyTimeToLiveAsync(host.Native("clock-drift")).WaitAsync(Ct);
        if (expires)
        {
            ttl.Should().NotBeNull().And.BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        }
        else
        {
            ttl.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_completed_write_wins_over_a_mover_holding_an_older_value()
    {
        await using var host = await MovingCacheHost.CreateAsync(fixture);
        await host.SeedAsync("contended", [1]);
        await EnterMovingAsync(host);

        var ledger = new Dictionary<string, List<long>?>();
        var promotion = new PausingSchedule(SchedulePointStep.Publish);
        await host.StartMoverAsync(schedule: promotion);
        try
        {
            await promotion.ReachedAsync.WaitAsync(Ct);
            List<long> value = [2];
            await host.WriteAsync("contended", value);
            ledger["contended"] = value;
            await host.AssertValueAsync("contended", value);
        }
        finally
        {
            promotion.Release();
        }

        await host.SetStageAsync(4);
        await host.AdvanceAsync("S4");
        await host.AssertNativeLedgerAsync(ledger);
        (await host.LegacyKeysAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Oversized_no_origin_data_blocks_drain_until_the_application_rewrites_it()
    {
        await using var host = await MovingCacheHost.CreateAsync(fixture);
        var count = new KeyMoveOptions().MaxValueBytes / 20 + 1;
        List<long> oversized = [.. Enumerable.Repeat(long.MaxValue, count)];
        await host.SeedAsync("oversized", oversized);
        await EnterMovingAsync(host);
        await host.StartMoverAsync();
        await host.WaitForMoverPassAsync();
        (await ProgressAsync(host, "skipped:too_large")).Should().BePositive("the mover must classify the oversized key");
        await host.StopMoverAsync();
        await host.SetStageAsync(4);
        await host.ExpectDrainBlockedAsync();
        (await host.LegacyKeysAsync()).Should().Equal(host.Legacy("oversized"));
        (await host.NativeKeysAsync()).Should().BeEmpty();
        await host.AssertValueAsync("oversized", oversized);

        await host.WriteAsync("oversized", [42]);
        host.ExpireDrainInventory();
        await host.AdvanceAsync("S4");
        await host.AssertValueAsync("oversized", [42]);
        (await host.LegacyKeysAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unanswered_health_check_pauses_the_mover_while_the_application_keeps_working()
    {
        await using var host = await MovingCacheHost.CreateAsync(fixture);
        await host.SeedAsync("untouched", [17]);
        await host.SeedAsync("read-through", [23]);
        await EnterMovingAsync(host);
        var health = new UnresponsiveMoveHealth();
        var before = await ReadProgressAsync();
        var movedBefore = (long?)before.SingleOrDefault(entry => entry.Name == "moved").Value ?? 0;
        await host.StartMoverAsync(health);
        await health.Called.WaitAsync(Ct);

        await host.AssertValueAsync("read-through", [23]);
        await ExerciseTrafficAsync(host, "during-pause", 31);
        host.RateClock.Advance(await host.RateClock.NextTimerAsync(Ct));
        var pause = await host.MoverClock.NextTimerAsync(Ct);
        (await ReadProgressAsync()).Should().BeEquivalentTo(before, "the mover counters must stay unchanged while health is unanswered");

        await host.SetStageAsync(4);
        await host.ExpectAlertAsync("stage_pending");
        (await host.LegacyKeysAsync()).Should().Equal(host.Legacy("untouched"));
        (await host.Db.StringGetAsync(host.Legacy("untouched")).WaitAsync(Ct)).Should().Be((RedisValue)LegacyEnvelope.Write(new List<long> { 17 }));
        (await host.NativeKeysAsync()).Should().BeEquivalentTo(host.Native("read-through"), host.Native("during-pause"), host.Native("during-pause-fill"));

        health.Recover();
        await host.WaitForHealthCompletionAsync();
        host.RateClock.Advance(TimeSpan.FromSeconds(1));
        host.MoverClock.Advance(pause);
        await host.WaitForMoverPassAsync();
        (await ProgressAsync(host, "moved")).Should().BeGreaterThan(movedBefore);
        await host.AdvanceAsync("S4");
        (await host.LegacyKeysAsync()).Should().BeEmpty();
        await host.AssertValueAsync("untouched", [17]);

        async Task<HashEntry[]> ReadProgressAsync() =>
            await host.Db.HashGetAllAsync(host.Names.Progress(MovingCacheHost.Group), CommandFlags.DemandMaster).WaitAsync(Ct);
    }

    [Fact]
    public async Task Closing_with_accepted_no_origin_residue_keeps_exactly_the_acknowledged_legacy_key()
    {
        await using var host = await MovingCacheHost.CreateAsync(fixture, acceptRefill: true);
        await host.SeedAsync("movable", [17]);
        await EnterMovingAsync(host);
        await host.StartMoverAsync();
        await host.WaitForMoverPassAsync();
        await host.StopMoverAsync();

        await host.Db.StringSetAsync(host.Legacy("cached-null"), RedisValue.EmptyString).WaitAsync(Ct);
        await host.SetStageAsync(4);
        var drained = await host.AdvanceAsync("S4");
        drained.Residue.Should().ContainKey(MovingCacheHost.CacheId).WhoseValue.Should().Be(1);
        await host.ApproveAsync(ApprovalKinds.Close);
        await host.SetStageAsync(5);
        await host.AdvanceAsync("S5");

        (await host.LegacyKeysAsync()).Should().Equal(host.Legacy("cached-null"));
        (await host.Db.StringGetAsync(host.Legacy("cached-null")).WaitAsync(Ct)).Should().Be(RedisValue.EmptyString);
        await host.AssertNativeLedgerAsync(new Dictionary<string, List<long>?> { ["movable"] = [17], ["cached-null"] = null });
    }

    private static async Task EnterMovingAsync(MovingCacheHost host)
    {
        await host.PrepareMoveAsync();
        await host.SetStageAsync(3);
        await host.AdvanceAsync("S1");
        await host.AdvanceAsync("S2");
        await host.AdvanceAsync("S3");
    }

    private static async Task ExerciseTrafficAsync(MovingCacheHost host, string key, long version)
    {
        await host.WriteAsync(key, [version]);
        await host.AssertValueAsync(key, [version]);

        var deleted = key + "-delete";
        await host.WriteAsync(deleted, [version]);
        Assert.True(await host.Cache.RemoveAsync(deleted, Ct));
        await host.AssertValueAsync(deleted, null);

        var fill = key + "-fill";
        (await host.Cache.GetOrAddAsync(fill, _ => Task.FromResult<List<long>?>([version + 1]), Ct)).Should().Equal(version + 1);
        (await host.Cache.GetOrAddAsync(fill, _ => throw new InvalidOperationException("A stored fill was read as absent."), Ct)).Should().Equal(version + 1);
    }

    private static async Task<long> ProgressAsync(MovingCacheHost host, string field) =>
        (long?)(await host.Db.HashGetAsync(host.Names.Progress(MovingCacheHost.Group), field, CommandFlags.DemandMaster).WaitAsync(Ct)) ?? 0;

    private async Task MoveToClosedAsync(bool cluster)
    {
        await using var host = await MovingCacheHost.CreateAsync(fixture, cluster);
        const int writerCount = 4;
        var writers = Enumerable.Range(0, 16).Select(i => $"write-{i}").ToArray();
        string[] deletes = ["delete-0", "delete-1", "delete-2", "delete-3"];
        string[] hits = ["hit-0", "hit-1", "hit-2", "hit-3"];
        string[] untouched = cluster
            ? [.. Enumerable.Range(0, 128).Select(i => $"untouched-{i}").DistinctBy(key => host.Db.IdentifyEndpoint(host.Legacy(key)))]
            : ["untouched"];
        var deadline = host.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(5));
        var ledger = new ConcurrentDictionary<string, List<long>?>();
        foreach (var key in writers.Concat(deletes).Concat(hits).Concat(untouched))
        {
            var value = new List<long> { ledger.Count, -71 };
            await host.SeedAsync(key, value, deadline);
            ledger[key] = value;
        }

        if (cluster)
        {
            var layout = await host.Topology.ReadLayoutAsync(Ct);
            layout.Masters.Should().HaveCount(3).And.OnlyContain(master => master.ConfiguredReplicas == 0);
            untouched.Should().HaveCount(layout.Masters.Count);
            ledger.Keys.Select(key => host.Db.IdentifyEndpoint(host.Native(key))).Distinct().Should().HaveCount(3);
        }

        await host.AssertValueAsync(writers[0], ledger[writers[0]]);
        (await host.NativeKeysAsync()).Should().BeEmpty();
        await host.PrepareMoveAsync();
        var unexpectedNative = host.Native("before-start");
        await host.Db.StringSetAsync(unexpectedNative, "[999]").WaitAsync(Ct);
        await host.SetStageAsync(3);
        await host.ExpectAlertAsync("start_blocked", KeyMoveGateIds.StartNativeEmpty);
        await host.Db.KeyDeleteAsync(unexpectedNative).WaitAsync(Ct);
        await host.AdvanceAsync("S1");
        Assert.True(await host.SetAsync("ready", [71], deadline, Ct));
        ledger["ready"] = [71];
        await host.AdvanceAsync("S2");
        await host.AssertValueAsync("ready", ledger["ready"]);
        Assert.False(await host.SetAsync("ready", [72], null, Ct));
        await host.AdvanceAsync("S3");

        var mutateDuringMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var legacyHitsRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removals = 0;
        var operations = writers.Chunk(writers.Length / writerCount).Select<string[], Func<CancellationToken, Task>>(keys => token => WriteSeededKeys(keys, token)).ToList();
        operations.Add(DeleteSeededKeys);
        operations.Add(ReadSeededKeys);
        var promotion = new PausingSchedule(SchedulePointStep.Publish);
        await using (var traffic = new CacheTraffic(operations))
        {
            try
            {
                await traffic.RunUntilAsync(legacyHitsRead.Task.WaitAsync(Ct));
                await host.StartMoverAsync(schedule: promotion);
                await traffic.RunUntilAsync(promotion.ReachedAsync.WaitAsync(Ct));
                mutateDuringMove.TrySetResult();
                await traffic.WaitForStartAsync();
                await host.SetStageAsync(4);
                await host.ExpectAlertAsync("stage_pending");
            }
            finally
            {
                mutateDuringMove.TrySetResult();
                promotion.Release();
            }

            traffic.Continue();
            await traffic.RunUntilAsync(host.WaitForMoverPassAsync());
            (await ProgressAsync(host, "moved")).Should().BePositive("the mover must promote a key during the concurrent traffic");
            await traffic.FinishAsync();
            removals.Should().Be(2 * deletes.Length);
        }

        var drained = await host.AdvanceAsync("S4");
        drained.Residue.Should().ContainKey(MovingCacheHost.CacheId).WhoseValue.Should().Be(0);
        (await host.LegacyKeysAsync()).Should().BeEmpty();

        await host.SetStageAsync(3);
        var resumed = await host.AdvanceAsync("S3");
        resumed.MoveId.Should().NotBe(drained.MoveId);
        await host.SetStageAsync(4);
        await host.AdvanceAsync("S4");

        await host.SetStageAsync(5);
        await host.ExpectAlertAsync("close_pending");
        await host.ApproveAsync(ApprovalKinds.Close);
        await host.AdvanceAsync("S5");

        List<long> closedValue = [301];
        Assert.True(await host.SetAsync("closed", closedValue, deadline, Ct));
        ledger["closed"] = closedValue;
        await host.AssertValueAsync("closed", closedValue);

        await host.AssertNativeLedgerAsync(ledger);
        foreach (var (key, value) in ledger)
        {
            if (value is not null)
            {
                await host.AssertNativeBytesMatchLibraryWriteAsync(key, value, deadline);
            }
        }

        (await host.LegacyKeysAsync()).Should().BeEmpty();

        async Task WriteSeededKeys(string[] keys, CancellationToken token)
        {
            await mutateDuringMove.Task.WaitAsync(token);
            foreach (var key in keys)
            {
                List<long> next = [ledger[key]![0] + 1, -11];
                Assert.True(await host.SetAsync(key, next, deadline, token));
                ledger[key] = next;
                await host.AssertValueCoreAsync(key, next, token);
            }
        }

        async Task DeleteSeededKeys(CancellationToken token)
        {
            await mutateDuringMove.Task.WaitAsync(token);
            foreach (var key in deletes)
            {
                Assert.True(await host.Cache.RemoveAsync(key, token));
                removals++;
                ledger[key] = null;
                await host.AssertValueCoreAsync(key, null, token);
            }
        }

        async Task ReadSeededKeys(CancellationToken token)
        {
            foreach (var key in hits)
            {
                var actual = await host.Cache.GetOrAddAsync(key, _ => throw new InvalidOperationException($"Seed {key} was read as absent."), token);
                actual.Should().Equal(ledger[key], $"the existing legacy value for {key} must be returned");
            }

            legacyHitsRead.TrySetResult();
        }
    }

    private sealed class SettlingInventoryTopology(Func<CancellationToken, Task<ClusterLayout>> readLayout) : IStateTopology
    {
        public int Reads { get; private set; }

        public async Task<ClusterLayout> ReadLayoutAsync(CancellationToken cancellationToken)
        {
            var layout = await readLayout(cancellationToken);
            return ++Reads == 1 ? layout.WithUnsettled([5]) : layout;
        }

        public Task<MasterProbe> ProbeAsync(EndPoint master, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IMasterChannel> OpenAsync(EndPoint master, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
