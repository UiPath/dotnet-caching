using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The mover runs only in a forward S3 whose identity every node acknowledged, and stops when the state moves on.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoverStateWindowTests(RedisFixture fixture)
{
    private static readonly MoverOptions Small = new()
    {
        ScanCount = 5,
        PollInterval = TimeSpan.FromMilliseconds(40),
        PassPause = TimeSpan.FromHours(1),
        MinScanInterval = TimeSpan.Zero,
    };

    [Theory]
    [InlineData("S2", true)]
    [InlineData("S3", false)]
    [InlineData("S4", true)]
    public async Task Nothing_runs_in_S2_before_the_acknowledgement_or_after_S3(string state, bool acked)
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("a", "b");
        var host = kit.Host(Small, state: () => kit.State(state, acked));

        host.Start();
        try
        {
            await Task.Delay(600, TestContext.Current.CancellationToken);
            host.RunningUnits.Should().BeEmpty();
        }
        finally
        {
            await host.StopAsync();
        }

        (await kit.Kit.ExistsAsync(kit.Kit.L("a"))).Should().BeTrue();
        (await kit.Kit.ExistsAsync(kit.Kit.N("a"))).Should().BeFalse();
        (await kit.PassedAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task S3_after_the_acknowledgement_runs_and_starts_once_the_acknowledgement_arrives()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("a", "b");
        var acked = false;
        var host = kit.Host(Small, state: () => kit.State("S3", acked));

        host.Start();
        try
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            (await kit.PassedAsync()).Should().BeFalse();
            acked = true;
            await MoverKit.WaitAsync(kit.PassedAsync);
        }
        finally
        {
            await host.StopAsync();
        }

        (await kit.Kit.GetAsync(kit.Kit.N("a"))).Should().Be("v-a");
    }

    [Theory]
    [InlineData("S4")]
    [InlineData("newid")]
    [InlineData("newmove")]
    public async Task A_state_change_in_the_middle_of_a_pass_stops_it_at_the_next_batch_and_records_no_pass(string change)
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var keys = Enumerable.Range(0, 60).Select(i => $"k{i}").ToArray();
        await kit.SeedAsync(keys);
        var current = () => kit.State();
        var gate = new Gate(kit.Target, blockAt: 12);
        var host = kit.Host(Small, _ => gate, () => current());

        host.Start();
        try
        {
            await gate.ReachedAsync;
            current = change switch
            {
                "S4" => () => kit.State("S4"),
                "newid" => () => kit.State() with { Id = "other", AllAck = "other" },
                _ => () => kit.State() with { MoveId = MoverKit.MoveId + 1 },
            };
            gate.Release();
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }
        finally
        {
            gate.Release();
            await host.StopAsync();
        }

        (await kit.PassedAsync()).Should().BeFalse();
        var left = 0;
        foreach (var key in keys)
        {
            left += await kit.Kit.ExistsAsync(kit.Kit.L(key)) ? 1 : 0;
        }

        if (change != "newmove")
        {
            left.Should().BeGreaterThan(0, "the pass stopped before it moved every key");
        }

        // A new move id starts afresh under its own cursor, so only the old move's pass is proved absent.
    }

    private sealed class Gate(IMoveTarget inner, int blockAt) : IMoveTarget
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;

        public Task ReachedAsync => _reached.Task;

        public string CacheId => inner.CacheId;

        public bool NoOrigin => inner.NoOrigin;

        public ValueTask<DryRunProbe> ProbeAsync(string key, TimeSpan minTtl, bool decode, CancellationToken cancellationToken) => inner.ProbeAsync(key, minTtl, decode, cancellationToken);

        public void Release() => _released.TrySetResult();

        public async ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _seen) == blockAt)
            {
                _reached.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }

            return await inner.MoveAsync(key, minTtl, identity, ownership, cancellationToken);
        }
    }
}
