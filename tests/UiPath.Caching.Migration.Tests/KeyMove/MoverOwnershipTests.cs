using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A unit's lease covers one key's whole promotion: a holder that loses it in the middle of a key writes nothing more, and the key is left for the next owner. The lease lifetimes run on the manual clock.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoverOwnershipTests(RedisFixture fixture)
{
    private static readonly MoverOptions Options = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(40),
        PassPause = TimeSpan.FromHours(1),
        MinScanInterval = TimeSpan.Zero,
        LeaseTtl = TimeSpan.FromSeconds(30),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Capture")]
    [InlineData("Publish")]
    [InlineData("CompareDelete")]
    public async Task A_holder_whose_lease_lapses_and_is_taken_during_one_keys_promotion_sends_no_further_write_and_the_next_owner_moves_the_key(string step)
    {
        var pausedAt = Enum.Parse<SchedulePointStep>(step);
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var pause = new PausingSchedule(pausedAt);
        KeyRecordingDatabase? recorder = null;
        await using var kit = await MoverKit.CreateAsync(fixture, schedule: pause, clock: clock, interceptDb: db => KeyRecordingDatabase.Over(db, out recorder));
        await kit.SeedAsync("k0");
        var done = new DoneTarget(kit.Target);
        var host = kit.Host(Options, _ => done);
        var lease = kit.Kit.Names.UnitLease(kit.Kit.Group, (await kit.UnitIdsAsync()).Single());
        var (l, n) = (kit.Kit.L("k0"), kit.Kit.N("k0"));
        host.Start();
        try
        {
            await pause.ReachedAsync.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            // The holder is stalled past its lease, and another holder takes the unit: the lease key is gone and held by someone else.
            clock.Advance(Options.LeaseTtl + TimeSpan.FromSeconds(1));
            await kit.Db.KeyDeleteAsync(lease);
            (await kit.Kit.Scripts.LeaseAsync(lease, "the-next-holder", 600_000)).Should().Be(MoveScripts.LeaseAcquired);
            var before = recorder!.Touched.Count;
            pause.Release();
            await MoverKit.WaitAsync(() => Task.FromResult(done.Results.Count == 1));

            done.Results.Single().SkipReason.Should().Be(MoveResult.OwnershipLost);
            var sent = recorder.Touched.Skip(before).ToArray();
            sent.Where(k => k.Contains(n, StringComparison.Ordinal) || k.Contains(l, StringComparison.Ordinal)).Should().BeEmpty("no write to the key's fence, N or L after the lease was lost");
            (await kit.Kit.ExistsAsync(l)).Should().BeTrue();
            (await kit.Kit.ExistsAsync(n)).Should().Be(pausedAt == SchedulePointStep.CompareDelete, "only a publish made before the lease was lost is there");
            (await kit.ProgressAsync()).Should().NotContainKey("moved");
            (await kit.PassedAsync()).Should().BeFalse();
            (await kit.Db.StringGetAsync(lease)).ToString().Should().Be("the-next-holder");

            // The next owner leaves the unit, and the holder takes it again from where it stands: the key is moved.
            await kit.Db.KeyDeleteAsync(lease);
            clock.Advance(Options.PollInterval);
            await MoverKit.WaitAsync(kit.PassedAsync);
        }
        finally
        {
            pause.Release();
            await host.StopAsync();
        }

        (await kit.Kit.GetAsync(n)).Should().Be("v-k0");
        (await kit.Kit.ExistsAsync(l)).Should().BeFalse();
    }

    [Fact]
    public async Task A_state_that_leaves_forward_S3_during_one_keys_promotion_stops_it_before_the_publish()
    {
        var pause = new PausingSchedule(SchedulePointStep.Publish);
        KeyRecordingDatabase? recorder = null;
        await using var kit = await MoverKit.CreateAsync(fixture, schedule: pause, interceptDb: db => KeyRecordingDatabase.Over(db, out recorder));
        await kit.SeedAsync("k0");
        var done = new DoneTarget(kit.Target);
        var host = kit.Host(Options, _ => done);
        host.Start();
        try
        {
            await pause.ReachedAsync.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            kit.Kit.ApplyRules("S4");
            var before = recorder!.Touched.Count;
            pause.Release();
            await MoverKit.WaitAsync(() => Task.FromResult(done.Results.Count == 1));

            done.Results.Single().SkipReason.Should().Be(MoveResult.OwnershipLost);
            recorder.Touched.Skip(before).Where(k => k.Contains(kit.Kit.N("k0"), StringComparison.Ordinal)).Should().BeEmpty();
        }
        finally
        {
            pause.Release();
            await host.StopAsync();
        }

        (await kit.Kit.ExistsAsync(kit.Kit.N("k0"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.Kit.L("k0"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_promotion_that_outlasts_a_third_of_the_lease_renews_it_before_it_writes_and_goes_on()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var pause = new PausingSchedule(SchedulePointStep.Publish);
        KeyRecordingDatabase? recorder = null;
        await using var kit = await MoverKit.CreateAsync(fixture, schedule: pause, clock: clock, interceptDb: db => KeyRecordingDatabase.Over(db, out recorder));
        await kit.SeedAsync("k0");
        var host = kit.Host(Options);
        var lease = kit.Kit.Names.UnitLease(kit.Kit.Group, (await kit.UnitIdsAsync()).Single());
        string[] sent;
        host.Start();
        try
        {
            await pause.ReachedAsync.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            // The stalled promotion has used more than a third of the lease and less than all of it.
            clock.Advance(Options.LeaseTtl / 2);
            var before = recorder!.Touched.Count;
            pause.Release();
            await MoverKit.WaitAsync(kit.PassedAsync);
            sent = [.. recorder.Touched.Skip(before)];
        }
        finally
        {
            pause.Release();
            await host.StopAsync();
        }

        var renewed = Array.IndexOf(sent, lease.ToString());
        var published = Array.FindIndex(sent, k => k.Contains(kit.Kit.N("k0"), StringComparison.Ordinal));
        renewed.Should().BeGreaterThanOrEqualTo(0).And.BeLessThan(published, "the lease is renewed before the publish is sent");
        (await kit.Kit.GetAsync(kit.Kit.N("k0"))).Should().Be("v-k0");
        (await kit.ProgressAsync())["moved"].Should().Be(1);
    }

    [Fact]
    public async Task A_lease_that_is_taken_while_the_state_is_being_read_stops_the_promotion_before_it_writes()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var pause = new PausingSchedule(SchedulePointStep.Publish);
        KeyRecordingDatabase? recorder = null;
        await using var kit = await MoverKit.CreateAsync(fixture, schedule: pause, clock: clock, interceptDb: db => KeyRecordingDatabase.Over(db, out recorder));
        await kit.SeedAsync("k0");
        var done = new DoneTarget(kit.Target);
        var armed = 0;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = kit.Host(Options, _ => done, readState: async _ =>
        {
            if (Volatile.Read(ref armed) == 1 && reached.TrySetResult())
            {
                await released.Task;
            }

            return kit.State();
        });
        var lease = kit.Kit.Names.UnitLease(kit.Kit.Group, (await kit.UnitIdsAsync()).Single());
        host.Start();
        try
        {
            await pause.ReachedAsync.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            // More than a third of the lease is spent, so the check renews and then reads the state, which is held here.
            clock.Advance(Options.LeaseTtl / 2);
            Volatile.Write(ref armed, 1);
            var before = recorder!.Touched.Count;
            pause.Release();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            // While the read is out, the renewed lease lapses and another holder takes the unit.
            clock.Advance(Options.LeaseTtl + TimeSpan.FromSeconds(1));
            await kit.Db.KeyDeleteAsync(lease);
            (await kit.Kit.Scripts.LeaseAsync(lease, "the-next-holder", 600_000)).Should().Be(MoveScripts.LeaseAcquired);
            released.SetResult();
            await MoverKit.WaitAsync(() => Task.FromResult(done.Results.Count == 1));

            done.Results.Single().SkipReason.Should().Be(MoveResult.OwnershipLost);
            recorder.Touched.Skip(before).Where(k => k.Contains(kit.Kit.N("k0"), StringComparison.Ordinal)).Should().BeEmpty("no publish after the lease was lost");
            (await kit.Kit.ExistsAsync(kit.Kit.N("k0"))).Should().BeFalse();
        }
        finally
        {
            released.TrySetResult();
            pause.Release();
            await host.StopAsync();
        }
    }
}
