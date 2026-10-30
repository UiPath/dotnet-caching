using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

/// <summary>Two nodes on one Redis with broadcast: a peer's write refreshes the local copy instead of dropping it.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class PeerRefreshIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private readonly string _app = $"pr{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_peer_keeps_serving_its_copy_and_then_serves_the_new_value()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetry = new RecordingTelemetryProvider();
        var writer = Node("a");
        var peer = Node("b", telemetry);
        var before = await WarmAsync(writer, peer, telemetry);
        var key = Unique();
        // The peer's own write is not heard back, so no event of the old value can reach the copy.
        (await peer.SetAsync(key, "old", Ct)).Should().BeTrue();
        (await peer.GetAsync<string>(key, Ct)).Should().Be("old");

        (await writer.SetAsync(key, "new", Ct)).Should().BeTrue();
        var seen = new List<string?>();
        var arrived = await EventuallyAsync(async () =>
        {
            var value = await peer.GetAsync<string>(key, Ct);
            seen.Add(value);
            return value == "new";
        });

        arrived.Should().BeTrue();
        seen.Should().OnlyContain(v => v == "old" || v == "new", "the peer serves its copy until the refresh replaces it, and never misses");
        (await EventuallyAsync(() => Task.FromResult(telemetry.Metrics.Skip(before).Any(m => m.Name == "cache.peer_refresh.kept")))).Should().BeTrue("the peer's own refresh read installed the new value");
        telemetry.Metrics.Skip(before).Where(m => m.Name == "cache.peer_refresh.started").Sum(m => m.Value).Should().Be(1, "a drop-and-read-through would start no refresh");
    }

    [Fact]
    public async Task A_peer_batch_of_20_keys_in_one_slot_is_refreshed_in_at_most_two_reads()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetry = new RecordingTelemetryProvider();
        var writer = Node("a");
        var peer = Node("b", telemetry);
        var before = await WarmAsync(writer, peer, telemetry);
        var keys = Enumerable.Range(0, 20).Select(i => (CacheKey)$"{{t{_app}}}-{i}-{Guid.NewGuid():N}").ToArray();
        // The peer's own write is not heard back, so no event of the old values can reach the copies.
        (await peer.SetManyAsync(keys.Select(k => new KeyValuePair<CacheKey, string?>(k, "old")), (CachePolicy?)null, Ct)).Should().BeEmpty();
        foreach (var key in keys)
        {
            (await peer.GetAsync<string>(key, null, Ct)).Should().Be("old");
        }

        (await writer.SetManyAsync(keys.Select(k => new KeyValuePair<CacheKey, string?>(k, "new")), (CachePolicy?)null, Ct)).Should().BeEmpty();

        foreach (var key in keys)
        {
            (await EventuallyAsync(async () => await peer.GetAsync<string>(key, null, Ct) == "new")).Should().BeTrue($"the peer refreshes {key.Name}");
        }

        (await EventuallyAsync(() => Task.FromResult(telemetry.Metrics.Skip(before).Where(m => m.Name == "cache.peer_refresh.started").Sum(m => m.Value) >= 20))).Should().BeTrue();
        telemetry.Metrics.Skip(before).Where(m => m.Name == "cache.peer_refresh.started").Sum(m => m.Value).Should().Be(20);
        telemetry.Metrics.Skip(before).Count(m => m.Name == "cache.peer_refresh.read_ms").Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task A_removal_still_drops_on_the_peer()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetry = new RecordingTelemetryProvider();
        var writer = Node("a");
        var peer = Node("b", telemetry);
        var before = await WarmAsync(writer, peer, telemetry);
        var key = Unique();
        (await peer.SetAsync(key, "v", Ct)).Should().BeTrue();
        (await peer.GetAsync<string>(key, Ct)).Should().Be("v");

        await writer.RemoveAsync<string>(key, Ct);

        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) is null)).Should().BeTrue("a removal is dropped, not refreshed");
        telemetry.Metrics.Skip(before).Should().NotContain(m => m.Name == "cache.peer_refresh.started", "a removal starts no refresh");
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // The peer's subscription starts while its first read returns; a write before its consumer group exists is reported as a gap that clears the peer's copies instead of refreshing them. Returns the telemetry metrics the warm-up produced, which the test skips.
    private static async Task<int> WarmAsync(ICache writer, ICache peer, RecordingTelemetryProvider telemetry)
    {
        var key = Unique();
        // The peer writes the old value itself, so only the writer's one event can reach its copy.
        (await peer.SetAsync(key, "old", Ct)).Should().BeTrue();
        (await peer.GetAsync<string>(key, Ct)).Should().Be("old");
        (await writer.SetAsync(key, "new", Ct)).Should().BeTrue();
        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "new")).Should().BeTrue("the peer's subscription to the topic is live");

        // The refresh the warm-up's own write may have started ends, and is reported, before the test counts from here.
        var cache = (MultilayerCache)peer;
        (await EventuallyAsync(() => Task.FromResult(cache.RunsInFlight == 0))).Should().BeTrue("the warm-up's refresh ended");
        cache.FlushPeerRefreshMetrics();
        return telemetry.Metrics.Count;
    }

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(20, Ct);
        }

        return false;
    }

    private ICache Node(string source, ICachingTelemetryProvider? telemetry = null)
    {
        var services = new ServiceCollection();
        if (telemetry is not null)
        {
            services.AddSingleton(telemetry);
        }

        var provider = services
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis();
                    b.AddBroadcast();
                    b.AddInMemoryRedis(o => o.LocalMaxExpiration = TimeSpan.FromMinutes(1));
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.SourceUri = new Uri($"urn:{source}-{_app}");
                })
            .BuildServiceProvider();
        _nodes.Add(provider);
        return provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis);
    }
}
