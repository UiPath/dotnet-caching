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
        var poll = kit.PollClock(host);
        for (var i = 0; i < 20; i++)
        {
            await poll.AdvanceNextTimerAsync(TestContext.Current.CancellationToken);
        }

        await poll.NextTimerAsync(TestContext.Current.CancellationToken);
        await host.StopAsync();

        Volatile.Read(ref reads).Should().Be(0, "the node's rules already say no move acts on the group");
        units.Lists.Should().Be(0);
        host.RunningUnits.Should().BeEmpty();
    }

    [Fact]
    public async Task The_next_mover_poll_waits_for_the_previous_runner_to_stop()
    {
        var token = TestContext.Current.CancellationToken;
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("k");
        var acked = true;
        var target = new StoppingTarget(kit.Target);
        var host = kit.Host(wrap: _ => target, state: () => kit.State(acked: Volatile.Read(ref acked)));
        var poll = kit.PollClock(host);
        host.Start();
        try
        {
            await target.Moving.Task.WaitAsync(token);
            Volatile.Write(ref acked, false);
            await poll.AdvanceNextTimerAsync(token);
            await target.Stopping.Task.WaitAsync(token);
            Volatile.Write(ref acked, true);

            var timestamp = poll.GetTimestamp();
            var nextPoll = MovingCacheHost.AdvanceMoverPollAsync(poll);
            poll.GetTimestamp().Should().Be(timestamp, "reconciliation is still stopping the previous runner");

            target.Release.TrySetResult();
            await nextPoll.WaitAsync(token);
            await kit.WaitForPassAsync(host);
        }
        finally
        {
            target.Release.TrySetResult();
            await host.StopAsync();
        }

        (await kit.Kit.GetAsync(kit.Kit.N("k"))).Should().Be("v-k");
    }

    private sealed class StoppingTarget(IMoveTarget inner) : IMoveTarget
    {
        public TaskCompletionSource Moving { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string CacheId => inner.CacheId;

        public bool NoOrigin => inner.NoOrigin;

        public ValueTask<DryRunProbe> ProbeAsync(string key, TimeSpan minTtl, bool decode, CancellationToken cancellationToken) =>
            inner.ProbeAsync(key, minTtl, decode, cancellationToken);

        public async ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, Action<long, long>? observeSourceTtl, CancellationToken cancellationToken)
        {
            if (Moving.TrySetResult())
            {
                using var registration = cancellationToken.Register(() => Stopping.TrySetResult());
                await Release.Task;
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await inner.MoveAsync(key, minTtl, identity, ownership, observeSourceTtl, cancellationToken);
        }
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
