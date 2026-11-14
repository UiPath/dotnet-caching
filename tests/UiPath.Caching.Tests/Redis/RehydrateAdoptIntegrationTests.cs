using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

/// <summary>Two nodes on one Redis: a node that was not told of a peer's refresh adopts the refreshed value instead of generating it again.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class RehydrateAdoptIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private static readonly CachePolicy Policy = new()
    {
        DistributedExpiration = TimeSpan.FromSeconds(30),
        RehydrateEnabled = true,
        Rehydrate = new RehydrateOptions
        {
            Threshold = 0.1,
            BaseCooldown = TimeSpan.FromSeconds(1),
            MaxCooldown = TimeSpan.FromMinutes(1),
            TimeoutFraction = 0.5,
            Name = "adopt-test",
        },
    };

    private readonly string _app = $"ra{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_node_not_told_of_a_peer_refresh_adopts_it_instead_of_generating_again()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var telemetryA = new RecordingTelemetryProvider();
        var telemetryB = new RecordingTelemetryProvider();
        var a = Node("a", broadcast: true, telemetryA);
        var b = Node("b", broadcast: false, telemetryB);
        var key = $"it-{Guid.NewGuid():N}";
        var generatedByA = 0;
        var generatedByB = 0;
        var versionOfA = "v1";
        Task<string?> GenerateA(CancellationToken _)
        {
            Interlocked.Increment(ref generatedByA);
            return Task.FromResult<string?>(versionOfA);
        }
        Task<string?> GenerateB(CancellationToken _)
        {
            Interlocked.Increment(ref generatedByB);
            return Task.FromResult<string?>("generated-by-b");
        }

        (await a.GetOrAddAsync(key, GenerateA, Policy, Ct)).Should().Be("v1");
        (await b.GetOrAddAsync(key, GenerateB, Policy, Ct)).Should().Be("v1");

        // Past the threshold of the 30 s lifetime (3 s), both copies are due.
        await Task.Delay(TimeSpan.FromSeconds(3.5), Ct);
        versionOfA = "v2";
        (await a.GetOrAddAsync(key, GenerateA, Policy, Ct)).Should().Be("v1");
        (await EventuallyAsync(() => Tracked(telemetryA, "cache.rehydrate.succeeded"))).Should().BeTrue("node a refreshed the key");
        generatedByA.Should().Be(2);
        (await LockGoneAsync(key)).Should().BeTrue("node a released the key's rehydrate lock, so node b can win it");

        var served = new List<string?>();
        (await EventuallyAsync(async () =>
        {
            var value = await b.GetOrAddAsync(key, GenerateB, Policy, Ct);
            served.Add(value);
            return value == "v2";
        })).Should().BeTrue("node b serves what a refreshed");

        generatedByB.Should().Be(0);
        served.Should().OnlyContain(v => v == "v1" || v == "v2");
        (await EventuallyAsync(() => Tracked(telemetryB, "cache.rehydrate.adopted"))).Should().BeTrue("the value is kept before the run reports it");
        Tracked(telemetryB, "cache.rehydrate.succeeded").Should().BeFalse();
        telemetryB.Metrics.Should().Contain(m => m.Name.Contains("ReadForAdoptionAsync", StringComparison.Ordinal), "the adoption read is the snapshot read of the built-in Redis cache");
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static bool Tracked(RecordingTelemetryProvider telemetry, string name) => telemetry.Events.Any(e => e.Name == name);

    private static Task<bool> EventuallyAsync(Func<bool> condition) => EventuallyAsync(() => Task.FromResult(condition()));

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

    private async Task<bool> LockGoneAsync(string key)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var server = connection.GetServer(connection.GetEndPoints()[0]);
        return await EventuallyAsync(() => !server.Keys(pattern: $"*{key}*").Any(k => k.ToString().Contains("rehydrate", StringComparison.Ordinal)));
    }

    private ICache Node(string source, bool broadcast, ICachingTelemetryProvider telemetry)
    {
        var provider = new ServiceCollection()
            .AddSingleton(telemetry)
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis();
                    b.AddRedisDistributedLock();
                    b.AddBroadcast();
                    b.AddInMemoryRedis(o =>
                    {
                        o.LocalMaxExpiration = TimeSpan.FromMinutes(1);
                        o.BroadcastEnable = broadcast;
                        // The node's first connection reports a restore, which would drop the copy the test relies on.
                        o.ClearLocalOnReconnect = false;
                    });
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
