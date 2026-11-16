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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fresh_inner_hash_is_kept_locally_and_the_generator_does_not_run()
    {
        using var c = new PeerCluster();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = (c.Add("a"), c.Add("b", telemetry: telemetry));
        await HoldingAged(c, a, b);
        var generated = new Generated();

        (await b.Cache.GetOrAddAsync<string>(Key, generated.Run, Rehydrating(), Ct)).Should().HaveCount(1);
        await PeerClusterUntil(() => Tracked(telemetry, Adopted));
        await b.Settled();

        generated.Calls.Should().Be(0);
        Held(b).Should().BeEquivalentTo(c.Redis.Fields());
    }

    [Fact]
    public async Task A_peer_field_update_applies_on_top_of_an_adopted_hash_without_another_read()
    {
        using var c = new PeerCluster();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = (c.Add("a"), c.Add("b", telemetry: telemetry));
        await HoldingAged(c, a, b);
        await b.Cache.GetOrAddAsync<string>(Key, new Generated().Run, Rehydrating(), Ct);
        await PeerClusterUntil(() => Tracked(telemetry, Adopted));
        await b.Settled();
        var reads = b.InnerReads;

        // The adopted copy carries the version of the read, so the next counted update continues it.
        (await a.Cache.SetAsync<string>(Key, new Dictionary<string, string?> { ["c"] = "3" }, new HashCacheEntryOptions(SetOption: HashCacheSetOption.HashReplace), Window, Ct)).Should().BeTrue();
        await b.Settled();

        b.InnerReads.Should().Be(reads);
        Held(b).Should().BeEquivalentTo(c.Redis.Fields());
        Held(b).Should().ContainKey("z").And.ContainKey("c");
    }

    [Fact]
    public async Task A_removal_after_the_inner_read_wins_over_the_adopted_hash()
    {
        using var c = new PeerCluster();
        var telemetry = new RecordingTelemetryProvider();
        var (a, b) = (c.Add("a"), c.Add("b", timeout: Timeout.InfiniteTimeSpan, telemetry: telemetry));
        await HoldingAged(c, a, b);
        var gate = new TaskCompletionSource();
        var generated = new Generated();
        var reads = b.InnerReads;
        b.Inner.HeldReadSnapshots = true;
        b.Inner.ReadHold = gate.Task;

        try
        {
            await b.Cache.GetOrAddAsync<string>(Key, generated.Run, Rehydrating(), Ct);
            await PeerClusterUntil(() => b.InnerReads == reads + 1);

            // The read in flight holds the refreshed hash when a peer removes the key.
            await a.Cache.RemoveAsync<string>(Key, Ct);
            await b.Settled();
            gate.SetResult();
            await PeerClusterUntil(() => Tracked(telemetry, Succeeded));
            await b.Settled();

            Tracked(telemetry, Adopted).Should().BeFalse("a fill the removal refused is not an adoption");
            generated.Calls.Should().Be(1);
            Held(b).Should().BeEquivalentTo(new Dictionary<string, string?> { ["f"] = "generated" }, "the adopted hash was not kept");
        }
        finally
        {
            gate.TrySetResult();
            await b.Settled();
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
    private static async Task HoldingAged(PeerCluster c, PeerCluster.Node a, PeerCluster.Node b)
    {
        var options = new HashCacheEntryOptions(ExpireTime: DateTimeOffset.UtcNow.AddMinutes(2));
        (await a.Cache.SetAsync<string>(Key, new Dictionary<string, string?> { ["f"] = "old" }, options, Window, Ct)).Should().BeTrue();
        (await b.Cache.GetAsync<string>(Key, Window, Ct)).Should().HaveCount(1);
        c.Redis.WriteQuietly(("z", "new"));
        c.Redis.SetExpirationQuietly(DateTimeOffset.UtcNow.AddMinutes(9));
    }

    private static IDictionary<string, string?>? Held(PeerCluster.Node node) =>
        node.Local.TryGetValue(Key, out ICacheEntry<IDictionary<string, string?>>? held) ? held!.Value : null;

    private static bool Tracked(RecordingTelemetryProvider telemetry, string name) => telemetry.Events.Any(e => e.Name == name);

    private static async Task PeerClusterUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(5, Ct);
        }

        condition().Should().BeTrue();
    }

    private sealed class Generated
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IDictionary<string, string?>> Run(CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = "generated" });
        }
    }
}
