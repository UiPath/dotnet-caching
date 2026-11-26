using Microsoft.Extensions.Caching.Memory;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests;

/// <summary>A node that wins the rehydrate lock for a hash keeps a whole hash another node already refreshed, unless the key was invalidated after the read.</summary>
public class MultilayerHashCacheRehydrateAdoptTests
{
    private const string Key = "k";
    private const string Adopted = "cache.rehydrate.adopted";
    private const string Succeeded = "cache.rehydrate.succeeded";

    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);
    private static readonly CachePolicy Window = new() { DistributedExpiration = Duration, LocalExpiration = Duration };

    private readonly ManualTimeProvider _clock = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fresh_inner_hash_is_kept_locally_and_the_generator_does_not_run()
    {
        using var c = new PeerCluster();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = (c.Add("a", clock: _clock), c.Add("b", telemetry: telemetry, clock: _clock));
        await HoldingAged(c, a, b, _clock);
        var generated = new Generated();

        (await b.Cache.GetOrAddAsync<string>(Key, generated.Run, Rehydrating(), Ct)).Should().HaveCount(1);
        await telemetry.Signal.UntilAsync(() => Tracked(telemetry, Adopted), Ct);
        await CacheWork.CompleteAsync(b.Cache);

        generated.Calls.Should().Be(0);
        Held(b).Should().BeEquivalentTo(c.Redis.Fields());
    }

    [Fact]
    public async Task A_peer_field_update_applies_on_top_of_an_adopted_hash_without_another_read()
    {
        using var c = new PeerCluster();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = (c.Add("a", clock: _clock), c.Add("b", telemetry: telemetry, clock: _clock));
        await HoldingAged(c, a, b, _clock);
        await b.Cache.GetOrAddAsync<string>(Key, new Generated().Run, Rehydrating(), Ct);
        await telemetry.Signal.UntilAsync(() => Tracked(telemetry, Adopted), Ct);
        await CacheWork.CompleteAsync(b.Cache);
        var reads = b.InnerReads;

        // The adopted copy carries the version of the read, so the next counted update continues it.
        (await a.Cache.SetAsync<string>(Key, new Dictionary<string, string?> { ["c"] = "3" }, new HashCacheEntryOptions(SetOption: HashCacheSetOption.HashReplace), Window, Ct)).Should().BeTrue();
        await CacheWork.CompleteAsync(b.Cache);

        b.InnerReads.Should().Be(reads);
        Held(b).Should().BeEquivalentTo(c.Redis.Fields());
        Held(b).Should().ContainKey("z").And.ContainKey("c");
    }

    [Fact]
    public async Task A_removal_after_the_inner_read_wins_over_the_adopted_hash()
    {
        using var c = new PeerCluster();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = (c.Add("a", clock: _clock), c.Add("b", timeout: Timeout.InfiniteTimeSpan, telemetry: telemetry, clock: _clock));
        await HoldingAged(c, a, b, _clock);
        var gate = new TaskCompletionSource();
        var generated = new Generated();
        var reads = b.InnerReads;
        b.Inner.HeldReadSnapshots = true;
        b.Inner.ReadHold = gate.Task;

        try
        {
            await b.Cache.GetOrAddAsync<string>(Key, generated.Run, Rehydrating(), Ct);
            await b.Inner.Changed.UntilAsync(() => b.InnerReads == reads + 1, Ct);

            // The read in flight holds the refreshed hash when a peer removes the key.
            await a.Cache.RemoveAsync<string>(Key, Ct);
            await CacheWork.CompleteAsync(b.Cache);
            gate.SetResult();
            await telemetry.Signal.UntilAsync(() => Tracked(telemetry, Succeeded), Ct);
            await CacheWork.CompleteAsync(b.Cache);

            Tracked(telemetry, Adopted).Should().BeFalse("a fill the removal refused is not an adoption");
            generated.Calls.Should().Be(1);
            Held(b).Should().BeEquivalentTo(new Dictionary<string, string?> { ["f"] = "generated" }, "the adopted hash was not kept");
        }
        finally
        {
            gate.TrySetResult();
            await CacheWork.CompleteAsync(b.Cache);
        }
    }

    private static CachePolicy Rehydrating() => new()
    {
        DistributedExpiration = Duration,
        LocalExpiration = Duration,
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

    /// <summary>Node <paramref name="b"/> holds a hash with a lifetime past the rehydrate threshold, which another node then refreshed without telling it.</summary>
    private static async Task HoldingAged(PeerCluster c, PeerCluster.Node a, PeerCluster.Node b, TimeProvider clock)
    {
        var options = new HashCacheEntryOptions(ExpireTime: clock.GetUtcNow().AddMinutes(2));
        (await a.Cache.SetAsync<string>(Key, new Dictionary<string, string?> { ["f"] = "old" }, options, Window, Ct)).Should().BeTrue();
        (await b.Cache.GetAsync<string>(Key, Window, Ct)).Should().HaveCount(1);
        c.Redis.WriteQuietly(("z", "new"));
        c.Redis.SetExpirationQuietly(clock.GetUtcNow().AddMinutes(9));
    }

    private static IDictionary<string, string?>? Held(PeerCluster.Node node) =>
        node.Local.TryGetValue(Key, out ICacheEntry<IDictionary<string, string?>>? held) ? held!.Value : null;

    private static bool Tracked(RecordingTelemetryProvider telemetry, string name) => telemetry.Events.Any(e => e.Name == name);



    private sealed class Generated
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

#pragma warning disable S1172 // bound as a generator delegate, which takes the token
        public Task<IDictionary<string, string?>> Run(CancellationToken token)
#pragma warning restore S1172
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = "generated" });
        }
    }
}
