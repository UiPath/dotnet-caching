using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests;

/// <summary>With an inner tier that leaves remove tombstones, a fill is told how long its generator ran, and one the tier refuses as removed while loading is returned and kept nowhere.</summary>
public class RemoveTombstoneFillTests
{
    private static readonly CacheKey Key = new("k");
    private static readonly CachePolicy Policy = new() { DistributedExpiration = TimeSpan.FromMinutes(10), LocalExpiration = TimeSpan.FromMinutes(10) };

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fill_is_told_how_long_its_generator_ran()
    {
        using var peers = new CachePeers { Fenced = true };
        var a = peers.Add("a", clock: _clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Generate(CancellationToken _)
        {
            started.SetResult();
            await release.Task;
            return "generated";
        }

        var call = a.Cache.GetOrAddAsync<string>(Key, Generate, Policy, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _clock.Advance(TimeSpan.FromSeconds(7));
        release.SetResult();

        (await call).Should().Be("generated");
        a.Inner.GeneratorAges.Should().ContainSingle().Which.Should().BeCloseTo(TimeSpan.FromSeconds(7), TimeSpan.FromMilliseconds(1));
        peers.Store.Values["k"].Should().Be("generated");
    }

    [Fact]
    public async Task A_tier_that_leaves_no_tombstone_is_told_nothing()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock);

        (await a.Cache.GetOrAddAsync<string>(Key, _ => Task.FromResult<string?>("generated"), Policy, Ct)).Should().Be("generated");

        a.Inner.GeneratorAges.Should().BeEmpty();
        peers.Store.Values["k"].Should().Be("generated");
    }

    [Fact]
    public async Task A_fill_removed_while_loading_returns_the_generated_value_and_keeps_it_nowhere()
    {
        using var peers = new CachePeers { Fenced = true };
        var telemetry = new RecordingTelemetryProvider();
        var a = peers.Add("a", telemetry: telemetry);
        a.Inner.RemovedWhileLoading = _ => true;

        (await a.Cache.GetOrAddAsync<string>(Key, _ => Task.FromResult<string?>("generated"), Policy, Ct)).Should().Be("generated", "the caller gets the value its generator made");

        peers.Store.Values.Should().NotContainKey("k");
        var reads = a.Inner.SingleReads;
        (await a.Cache.GetAsync<string>(Key, Policy, Ct)).Should().BeNull("this node did not keep it either");
        a.Inner.SingleReads.Should().Be(reads + 1, "the read went to the store");
        telemetry.Metrics.Where(m => m.Name == "cache.background_write.fill_refused" && m.Properties!["reason"] == "superseded").Sum(m => m.Value).Should().Be(1);
    }

    [Fact]
    public async Task A_background_fill_removed_while_loading_takes_back_the_copy_it_kept_ahead_of_the_write()
    {
        using var peers = new CachePeers { Fenced = true };
        var a = peers.Add("a");
        a.Inner.RemovedWhileLoading = _ => true;
        var policy = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(10), LocalExpiration = TimeSpan.FromMinutes(10), BackgroundDistributedWrite = true };

        (await a.Cache.GetOrAddAsync<string>(Key, _ => Task.FromResult<string?>("generated"), policy, Ct)).Should().Be("generated");
        await CachePeers.Until(() => a.Inner.GeneratorAges.Count == 1 && !CachePeers.WriteInFlight(a.Cache, "k"));

        var reads = a.Inner.SingleReads;
        (await a.Cache.GetAsync<string>(Key, policy, Ct)).Should().BeNull();
        a.Inner.SingleReads.Should().Be(reads + 1, "the local copy is gone, so the read went to the store");
        peers.Store.Values.Should().NotContainKey("k");
    }

    [Fact]
    public async Task A_batch_fill_is_told_the_longest_generator_age_and_leaves_out_a_key_removed_while_loading()
    {
        using var peers = new CachePeers { Fenced = true };
        var a = peers.Add("a", clock: _clock);
        a.Inner.RemovedWhileLoading = key => key.Name == "b";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<KeyValuePair<long, string?>[]> Generate(long[] states, CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return [.. states.Select(s => new KeyValuePair<long, string?>(s, $"gen:{s}"))];
        }

        var call = a.Cache.GetOrAddAsync<string, long>([new("a", 1L), new("b", 2L), new("c", 3L)], Generate, Policy, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _clock.Advance(TimeSpan.FromSeconds(3));
        release.SetResult();

        var result = await call;

        result.Select(r => r.Value).Should().BeEquivalentTo("gen:1", "gen:2", "gen:3");
        peers.Store.Values.Keys.Should().BeEquivalentTo("a", "c");
        a.Inner.GeneratorAges.Should().ContainSingle().Which.Should().BeCloseTo(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(1));
    }
}
