using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests;

/// <summary>A fill after a miss writes only an absent key, answers with the value found, and is refused when its key changed while its generator ran.</summary>
public class SafeFillTests
{
    private static readonly CacheKey Key = new("k");
    private static readonly CachePolicy Policy = new() { DistributedExpiration = TimeSpan.FromMinutes(10), LocalExpiration = TimeSpan.FromMinutes(10) };
    private static readonly TimeSpan LocalLifetime = TimeSpan.FromMilliseconds(100);

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Nodes_that_miss_together_return_and_keep_the_value_stored_first()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a");
        var b = peers.Add("b");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Generate(CancellationToken _)
        {
            await release.Task;
            return "from-a";
        }

        var slow = a.Cache.GetOrAddAsync<string>(Key, Generate, Policy, Ct).AsTask();

        (await b.Cache.GetOrAddAsync<string>(Key, _ => Task.FromResult<string?>("from-b"), Policy, Ct)).Should().Be("from-b");
        release.SetResult();

        (await slow).Should().Be("from-b", "the key was stored while a's generator ran, so a's value is discarded");
        var reads = a.Inner.SingleReads;
        (await a.Cache.GetAsync<string>(Key, Policy, Ct)).Should().Be("from-b");
        a.Inner.SingleReads.Should().Be(reads, "the stored value is kept locally");
        peers.Store.Values["k"].Should().Be("from-b");
        a.Published.Should().BeEmpty("a fill is quiet, a lost one too");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fill_whose_key_was_removed_while_its_generator_ran_stores_and_keeps_nothing(bool byPeer)
    {
        using var peers = new CachePeers();
        var a = peers.Add("a");
        var b = peers.Add("b");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Generate(CancellationToken _)
        {
            await release.Task;
            return "generated";
        }

        var slow = a.Cache.GetOrAddAsync<string>(Key, Generate, Policy, Ct).AsTask();

        await (byPeer ? b : a).Cache.RemoveAsync<string>(Key, Ct);
        release.SetResult();

        (await slow).Should().Be("generated", "the caller still gets what its generator made");
        peers.Store.Values.Should().BeEmpty("the removal came after the generator started, so what it made may be older than the removal");
        (await a.Cache.GetAsync<string>(Key, Policy, Ct)).Should().BeNull("nothing was kept locally either");
    }

    [Fact]
    public async Task A_fill_whose_key_was_written_while_its_generator_ran_leaves_the_written_value()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Generate(CancellationToken _)
        {
            await release.Task;
            return "generated";
        }

        var slow = a.Cache.GetOrAddAsync<string>(Key, Generate, Policy, Ct).AsTask();

        (await a.Cache.SetAsync<string>(Key, "set", Policy, Ct)).Should().BeTrue();
        release.SetResult();

        (await slow).Should().Be("generated");
        peers.Store.Values["k"].Should().Be("set");
        (await a.Cache.GetAsync<string>(Key, Policy, Ct)).Should().Be("set");
    }

    [Fact]
    public async Task A_value_a_fill_stored_is_not_kept_locally_when_a_peer_overwrites_it_before_the_keep()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a");
        var b = peers.Add("b");
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Inner.AfterWrite = landed.Task;
        var fill = a.Cache.GetOrAddAsync<string>(Key, _ => Task.FromResult<string?>("generated"), Policy, Ct).AsTask();
        await CachePeers.Until(() => peers.Store.Values.ContainsKey("k"));

        (await b.Cache.SetAsync<string>(Key, "peer", Policy, Ct)).Should().BeTrue();
        landed.SetResult();

        (await fill).Should().Be("generated");
        (await a.Cache.GetAsync<string>(Key, Policy, Ct)).Should().Be("peer", "the fill's keep was refused, so the read goes to the store");
    }

    [Fact]
    public async Task A_background_fill_that_finds_the_key_stored_replaces_the_copy_it_kept_ahead_of_the_write()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a");
        var policy = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(10), LocalExpiration = TimeSpan.FromMinutes(10), BackgroundDistributedWrite = true };
        Task<string?> Generate(CancellationToken _)
        {
            peers.Store.WriteQuietly("k", "stored");
            return Task.FromResult<string?>("generated");
        }

        (await a.Cache.GetOrAddAsync<string>(Key, Generate, policy, Ct)).Should().Be("generated", "the call returned before the write");
        var reads = a.Inner.SingleReads;
        var until = DateTime.UtcNow.AddSeconds(10);
        while (await a.Cache.GetAsync<string>(Key, policy, Ct) != "stored" && DateTime.UtcNow < until)
        {
            await Task.Delay(5, Ct);
        }

        (await a.Cache.GetAsync<string>(Key, policy, Ct)).Should().Be("stored", "the copy kept ahead of the write was replaced by the stored value");
        peers.Store.Values["k"].Should().Be("stored", "the fill did not overwrite it");
        a.Inner.SingleReads.Should().Be(reads, "both copies were local");
        a.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_late_fail_safe_value_that_finds_the_key_stored_keeps_the_stored_value_and_announces_nothing()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var policy = new CachePolicy { FailSafeEnabled = true, LocalExpiration = LocalLifetime, FailSafeTimeout = TimeSpan.FromMilliseconds(50) };
        (await a.Cache.GetOrAddAsync<string>(Key, _ => Task.FromResult<string?>("v1"), policy, Ct)).Should().Be("v1");
        await a.Settled();
        peers.Store.Values.Clear();
        _clock.Advance(LocalLifetime * 2);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = a.Cache.GetOrAddAsync<string>(Key, _ => release.Task, policy, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await call).Should().Be("v1");

        peers.Store.WriteQuietly("k", "v2");
        var announced = a.Published.Count;
        release.SetResult("late");
        await CachePeers.Until(() => a.Cache.LateGenerations == 0);
        await a.Settled();

        a.Published.Should().HaveCount(announced, "a late value that lost the write is not announced");
        peers.Store.Values["k"].Should().Be("v2", "the late value did not overwrite what a peer stored");
        var reads = a.Inner.SingleReads;
        (await a.Cache.GetAsync<string>(Key, policy, Ct)).Should().Be("v2");
        a.Inner.SingleReads.Should().Be(reads, "the stored value is kept locally");
    }

    [Fact]
    public async Task A_late_fail_safe_value_that_wins_the_write_is_announced()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a", clock: _clock, localClock: _clock);
        var policy = new CachePolicy { FailSafeEnabled = true, LocalExpiration = LocalLifetime, FailSafeTimeout = TimeSpan.FromMilliseconds(50) };
        (await a.Cache.GetOrAddAsync<string>(Key, _ => Task.FromResult<string?>("v1"), policy, Ct)).Should().Be("v1");
        await a.Settled();
        peers.Store.Values.Clear();
        _clock.Advance(LocalLifetime * 2);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = a.Cache.GetOrAddAsync<string>(Key, _ => release.Task, policy, Ct).AsTask();
        await CachePeers.Until(() => _clock.HasTimers);
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await call).Should().Be("v1");
        var announced = a.Published.Count;

        release.SetResult("late");
        await CachePeers.Until(() => a.Cache.LateGenerations == 0);
        await a.Settled();

        peers.Store.Values["k"].Should().Be("late");
        a.Published.Should().HaveCount(announced + 1, "peers in a cooldown serve the value it replaces");
    }

    [Fact]
    public async Task A_batch_fill_leaves_a_key_a_peer_stored_and_writes_the_others()
    {
        using var peers = new CachePeers();
        var a = peers.Add("a");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<KeyValuePair<long, string?>[]> Generate(long[] states, CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return [.. states.Select(s => new KeyValuePair<long, string?>(s, $"gen:{s}"))];
        }

        var call = a.Cache.GetOrAddAsync<string, long>([new("a", 1L), new("b", 2L)], Generate, Policy, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        peers.Store.WriteQuietly("a", "peer-a");
        release.SetResult();

        var result = await call;

        result.Single(r => r.Key == 1L).Value.Should().Be("peer-a", "the key a peer stored first is the answer");
        result.Single(r => r.Key == 2L).Value.Should().Be("gen:2");
        peers.Store.Values["a"].Should().Be("peer-a");
        peers.Store.Values["b"].Should().Be("gen:2");
        var reads = a.Inner.SingleReads + a.Inner.BatchReads;
        (await a.Cache.GetAsync<string>("a", Policy, Ct)).Should().Be("peer-a");
        (await a.Cache.GetAsync<string>("b", Policy, Ct)).Should().Be("gen:2");
        (a.Inner.SingleReads + a.Inner.BatchReads).Should().Be(reads, "both are kept locally");
    }

    [Fact]
    public async Task A_rehydration_does_not_overwrite_a_write_that_landed_while_its_generator_ran()
    {
        using var peers = new CachePeers();
        var telemetry = new RecordingTelemetryProvider();
        var b = peers.Add("b", telemetry: telemetry);
        var policy = Rehydrating();
        peers.Store.WriteQuietly("k", "old");
        peers.Store.Remaining["k"] = TimeSpan.FromMinutes(2);
        (await b.Cache.GetAsync<string>(Key, policy, Ct)).Should().Be("old");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Generate(CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return "generated";
        }

        await b.Cache.GetOrAddAsync<string>(Key, Generate, policy, Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        peers.Store.WriteQuietly("k", "peer");
        var announced = b.Published.Count;
        release.SetResult();
        await CachePeers.Until(() => telemetry.Events.Any(e => e.Name == "cache.rehydrate.succeeded"));
        await b.Settled();

        peers.Store.Values["k"].Should().Be("peer", "the rehydration compared against what its read saw, which a write has since replaced");
        b.Published.Should().HaveCount(announced, "a rehydration that lost announces nothing");
    }

    [Fact]
    public async Task A_rehydration_over_an_unchanged_value_writes_and_announces()
    {
        using var peers = new CachePeers();
        var b = peers.Add("b");
        var policy = Rehydrating();
        peers.Store.WriteQuietly("k", "old");
        peers.Store.Remaining["k"] = TimeSpan.FromMinutes(2);
        (await b.Cache.GetAsync<string>(Key, policy, Ct)).Should().Be("old");
        var announced = b.Published.Count;

        await b.Cache.GetOrAddAsync<string>(Key, _ => Task.FromResult<string?>("generated"), policy, Ct);
        await CachePeers.Until(() => peers.Store.Values["k"] as string == "generated");
        await b.Settled();

        b.Published.Should().HaveCount(announced + 1);
    }

    private static CachePolicy Rehydrating() => new()
    {
        DistributedExpiration = TimeSpan.FromMinutes(10),
        LocalExpiration = TimeSpan.FromMinutes(10),
        RehydrateEnabled = true,
        Rehydrate = new RehydrateOptions
        {
            Threshold = 0.75,
            BaseCooldown = TimeSpan.FromSeconds(1),
            MaxCooldown = TimeSpan.FromMinutes(5),
            TimeoutFraction = 0.5,
            Name = "safe-fill",
        },
    };
}
