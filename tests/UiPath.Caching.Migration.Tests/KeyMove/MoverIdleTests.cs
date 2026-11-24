using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A node runs a mover for every key move group, so a group with no move acting must cost the server nothing.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoverIdleTests(RedisFixture fixture)
{
    [Fact]
    public async Task A_group_that_no_move_acts_on_reads_no_state_and_lists_no_scan_units()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var reads = 0;
        var units = new CountingUnits(kit.Units);
        var idle = new StubPlane(kit.Kit.Group, new EpochGate());
        var host = kit.Host(
            MoverKit.Quick with { PollInterval = TimeSpan.FromMilliseconds(10) },
            plane: idle,
            units: units,
            readState: _ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult<StateRecord?>(kit.State());
            });

        host.Start();
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        await host.StopAsync();

        Volatile.Read(ref reads).Should().Be(0, "the node's rules already say no move acts on the group");
        units.Lists.Should().Be(0);
        host.RunningUnits.Should().BeEmpty();
    }

    private sealed class CountingUnits(IScanUnits inner) : IScanUnits
    {
        private int _lists;

        public int Lists => Volatile.Read(ref _lists);

        public Task<IReadOnlyList<ScanUnit>> ListAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _lists);
            return inner.ListAsync(cancellationToken);
        }
    }
}
