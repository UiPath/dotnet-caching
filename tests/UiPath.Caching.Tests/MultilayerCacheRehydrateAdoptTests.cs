using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests;

/// <summary>A node that wins the rehydrate lock keeps a value another node already refreshed in the inner tier, instead of running the generator.</summary>
public class MultilayerCacheRehydrateAdoptTests
{
    private const string Key = "k";
    private const string Adopted = "cache.rehydrate.adopted";
    private const string Succeeded = "cache.rehydrate.succeeded";

    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);
    private static readonly CachePolicy Warm = new() { DistributedExpiration = Duration, LocalExpiration = Duration };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fresh_inner_value_is_kept_locally_and_the_generator_does_not_run()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var b = peers.Add("b", telemetry: telemetry);
        await HoldingAged(peers, b, Warm);
        Refreshed(peers, "new");
        var generated = new Generated();

        (await b.Cache.GetOrAddAsync<string>(Key, generated.Run, Rehydrating(Warm), Ct)).Should().Be("old");
        await CachePeers.Until(() => Tracked(telemetry, Adopted));
        await b.Settled();

        generated.Calls.Should().Be(0);
        Tracked(telemetry, Succeeded).Should().BeFalse();
        (await b.Cache.GetAsync<string>(Key, Warm, Ct)).Should().Be("new");
    }

    [Theory]
    [InlineData("aged")]
    [InlineData("missing")]
    [InlineData("throws")]
    public async Task An_inner_value_that_is_not_fresh_enough_runs_the_generator_once(string inner)
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var b = peers.Add("b", telemetry: telemetry);
        await HoldingAged(peers, b, Warm);
        switch (inner)
        {
            case "aged":
                peers.Store.WriteQuietly(Key, "newer");
                break;
            case "missing":
                peers.Store.RemoveQuietly(Key);
                break;
            default:
                b.Inner.ReadThrows = true;
                break;
        }

        var generated = new Generated();

        await b.Cache.GetOrAddAsync<string>(Key, generated.Run, Rehydrating(Warm), Ct);
        await CachePeers.Until(() => Tracked(telemetry, Succeeded));
        await b.Settled();

        generated.Calls.Should().Be(1);
        Tracked(telemetry, Adopted).Should().BeFalse();
    }

    [Fact]
    public async Task A_disconnected_cache_does_not_read_the_inner_tier_and_runs_the_generator()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var b = peers.Add("b", telemetry: telemetry);
        await HoldingAged(peers, b, Warm);
        Refreshed(peers, "new");
        b.Inner.IsConnected = false;
        var reads = b.Inner.SingleReads;
        var generated = new Generated();

        await b.Cache.GetOrAddAsync<string>(Key, generated.Run, Rehydrating(Warm), Ct);
        await CachePeers.Until(() => Tracked(telemetry, Succeeded));
        await b.Settled();

        generated.Calls.Should().Be(1);
        b.Inner.SingleReads.Should().Be(reads);
        Tracked(telemetry, Adopted).Should().BeFalse();
    }

    [Fact]
    public async Task A_read_that_ignores_its_token_and_outlasts_the_budget_is_abandoned_and_the_generator_runs()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var b = peers.Add("b", new InMemoryRedisCacheOptions { Timeout = Timeout.InfiniteTimeSpan }, telemetry);
        await HoldingAged(peers, b, Warm);
        Refreshed(peers, "new");
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Inner.AfterNextRead = late.Task;
        // An empty value is not written, so nothing but the abandonment keeps the late read out of the local tier.
        var generated = new Generated(null);

        try
        {
            await b.Cache.GetOrAddAsync<string>(Key, generated.Run, Rehydrating(Warm), Ct);

            // The read is still in flight: only the budget lets the run go on to the generator.
            await CachePeers.Until(() => Tracked(telemetry, Succeeded));
            generated.Calls.Should().Be(1);

            late.SetResult();
            await Task.Delay(200, Ct);
            await b.Settled();

            Tracked(telemetry, Adopted).Should().BeFalse();
            (await b.Cache.GetAsync<string>(Key, Warm, Ct)).Should().Be("old", "the late read, a fresh \"new\", is not kept");
            telemetry.Events.Where(e => e.Name == "cache.rehydrate.adopt_read_failed").Select(e => e.Properties!["reason"]).Should().Equal("timeout");
        }
        finally
        {
            late.TrySetResult();
            await b.Settled();
        }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("removal")]
    public async Task An_invalidation_after_the_inner_read_refuses_the_adoption_and_the_generator_runs(string invalidation)
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var a = peers.Add("a");
        var b = peers.Add("b", new InMemoryRedisCacheOptions { Timeout = Timeout.InfiniteTimeSpan }, telemetry);
        await HoldingAged(peers, b, Warm);
        Refreshed(peers, "new");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = b.Inner.SingleReads;
        b.Inner.AfterNextRead = release.Task;
        var generated = new Generated();

        try
        {
            await b.Cache.GetOrAddAsync<string>(Key, generated.Run, Rehydrating(Warm), Ct);
            await CachePeers.Until(() => b.Inner.SingleReads == reads + 1);

            // The rehydrate read already holds "new" when a peer writes or removes the key.
            if (invalidation == "write")
            {
                (await a.Cache.SetAsync<string>(Key, "peer", Warm, Ct)).Should().BeTrue();
            }
            else
            {
                await a.Cache.RemoveAsync<string>(Key, Ct);
            }

            await b.Settled();
            release.SetResult();
            await CachePeers.Until(() => Tracked(telemetry, Succeeded));
            await b.Settled();

            Tracked(telemetry, Adopted).Should().BeFalse("a fill the invalidation refused is not an adoption");
            generated.Calls.Should().Be(1);
            (await b.Cache.GetAsync<string>(Key, Warm, Ct)).Should().Be("generated", "the adopted \"new\" was not kept");

            // The generated copy is fresh, so a later hit does not rehydrate again.
            await b.Cache.GetOrAddAsync<string>(Key, generated.Run, Rehydrating(Warm), Ct);
            await Task.Delay(300, Ct);
            generated.Calls.Should().Be(1);
            telemetry.Events.Count(e => e.Name == "cache.rehydrate.triggered").Should().Be(1);
        }
        finally
        {
            release.TrySetResult();
            await b.Settled();
        }
    }

    private static CachePolicy Rehydrating(CachePolicy policy) => new()
    {
        DistributedExpiration = policy.DistributedExpiration,
        LocalExpiration = policy.LocalExpiration,
        RehydrateEnabled = true,
        Rehydrate = new RehydrateOptions
        {
            Threshold = 0.75,
            BaseCooldown = TimeSpan.FromSeconds(1),
            MaxCooldown = TimeSpan.FromMinutes(5),
            TimeoutFraction = 0.5,
            Name = "test-profile",
        },
    };

    /// <summary>Node <paramref name="b"/> holds "old" locally with a lifetime past the rehydrate threshold.</summary>
    private static async Task HoldingAged(CachePeers peers, CachePeers.Node b, CachePolicy policy)
    {
        peers.Store.WriteQuietly(Key, "old");
        peers.Store.Remaining[Key] = TimeSpan.FromMinutes(2);
        (await b.Cache.GetAsync<string>(Key, policy, Ct)).Should().Be("old");
    }

    /// <summary>Another node refreshed the key and its event never reached b.</summary>
    private static void Refreshed(CachePeers peers, string value)
    {
        peers.Store.WriteQuietly(Key, value);
        peers.Store.Remaining[Key] = TimeSpan.FromMinutes(9);
    }

    private static bool Tracked(RecordingTelemetryProvider telemetry, string name) => telemetry.Events.Any(e => e.Name == name);

    private sealed class Generated(string? value = "generated")
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<string?> Run(CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(value);
        }
    }
}
