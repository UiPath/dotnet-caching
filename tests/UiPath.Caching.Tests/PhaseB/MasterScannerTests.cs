using System.Net;
using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

// A scan of the masters is complete or it fails: every slot owned, every owner connected, and the slot map unchanged while it ran.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class MasterScannerTests(RedisContainerFixture fixture)
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task A_layout_that_leaves_slots_without_a_master_fails_the_scan()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, true);
        kit.Kit.Topology.Layout = (real, _) => new ClusterLayout([new MasterLayout(real.Masters[0].Endpoint, [new SlotRange(0, 100)], 0)]);

        var act = () => kit.Data.CountNativeAsync(kit.Group, Ct);

        await act.Should().ThrowAsync<RedisException>().WithMessage("*101 of 16384*");
    }

    [Fact]
    public async Task A_master_that_is_not_connected_is_an_unavailable_owner_and_never_an_empty_one()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, true);
        await kit.SetAsync(MoveSide.Native, "a", "k1");
        kit.Kit.Topology.Layout = (_, _) => new ClusterLayout([new MasterLayout(new DnsEndPoint("down.invalid", 6379), [new SlotRange(0, HashSlot.Count - 1)], 0)]);

        var act = () => kit.Data.CountNativeAsync(kit.Group, Ct);

        await act.Should().ThrowAsync<RedisException>().WithMessage("*not connected*");
    }

    [Fact]
    public async Task A_slot_that_changes_owner_while_the_scan_ran_repeats_it_and_counts_the_repeat_once()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, true);
        await kit.SetAsync(MoveSide.Native, "a", "k1");
        await kit.SetAsync(MoveSide.Native, "b", "k2");
        kit.Kit.Topology.Layout = (real, n) => n == 2 ? Moved(real) : real;

        (await kit.Data.CountNativeAsync(kit.Group, Ct)).Should().Be(2, "the counts of the first attempt are dropped with it");
        (await kit.Census.RunAsync(kit.Group, Ct))!.Residue.Values.Sum().Should().Be(0, "the census scans the legacy side");
    }

    [Fact]
    public async Task A_slot_map_that_never_settles_fails_after_the_attempts()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, true);
        kit.Kit.Topology.Layout = (real, n) => n % 2 == 0 ? Moved(real) : real;

        var act = () => kit.Data.CountNativeAsync(kit.Group, Ct);

        await act.Should().ThrowAsync<RedisException>().WithMessage("*kept changing*");
    }

    [Fact]
    public async Task A_slot_in_migration_is_not_scanned_until_it_settles()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, true);
        await kit.SetAsync(MoveSide.Native, "a", "k1");
        kit.Kit.Topology.Layout = (real, n) => n == 1 ? real.WithUnsettled([5]) : real;

        (await kit.Data.CountNativeAsync(kit.Group, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task The_attempts_are_apart_by_the_retry_delay()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, true);
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var scanner = new MasterScanner(() => kit.Kit.Mux.GetServers(), kit.Kit.Topology, 0, string.Empty, time, TimeSpan.FromSeconds(5));
        kit.Kit.Topology.Layout = (real, n) => n == 2 ? Moved(real) : real;
        var attempts = 0;

        Task<int> Consume(IAsyncEnumerable<string> keys)
        {
            attempts++;
            return Task.FromResult(0);
        }

        var run = scanner.RunAsync(["nothing-*"], Consume, Ct);
        await TimeSteps.ArmedAsync(time);
        attempts.Should().Be(1, "the second attempt waits for the delay");
        time.Advance(TimeSpan.FromSeconds(5));
        await run;

        attempts.Should().Be(2);
    }

    private static ClusterLayout Moved(ClusterLayout real) => new(
        [new MasterLayout(real.Masters[0].Endpoint, [new SlotRange(0, HashSlot.Count - 2)], 0), new MasterLayout(new DnsEndPoint("other.invalid", 1), [new SlotRange(HashSlot.Count - 1, HashSlot.Count - 1)], 0)]);
}
