using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Broadcast.Redis;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class AutoAckIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private readonly string _app = $"aa{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_set_on_one_node_drops_the_peers_local_copy_and_leaves_no_pending_entries()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var a = Node("a");
        var b = Node("b");
        var key = (CacheKey)$"it-{Guid.NewGuid():N}";
        await a.SetAsync(key, "old", null, Ct);
        (await b.GetAsync<string>(key, null, Ct)).Should().Be("old");

        await a.SetAsync(key, "new", null, Ct);

        (await EventuallyAsync(async () => await b.GetAsync<string>(key, null, Ct) == "new")).Should().BeTrue("node b's local copy is invalidated");

        using var admin = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = admin.GetDatabase();
        var streams = admin.GetServer(admin.GetEndPoints()[0]).Keys(pattern: $"*{_app}*").ToArray();
        var groups = new List<StreamGroupInfo>();
        foreach (var stream in streams.Where(k => db.KeyType(k) == RedisType.Stream))
        {
            groups.AddRange(await db.StreamGroupInfoAsync(stream));
        }

        groups.Should().Contain(g => g.Name == $"urn:b-{_app}", "node b reads through its own group");
        groups.Should().OnlyContain(g => g.PendingMessageCount == 0, "NOACK leaves nothing in a group's pending list");
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }

        GC.SuppressFinalize(this);
    }

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

        return await condition();
    }

    private ICache Node(string source)
    {
        var provider = new ServiceCollection().AddLogging()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis();
                    b.AddRedisStreams(o => o.AutoAck = true);
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
