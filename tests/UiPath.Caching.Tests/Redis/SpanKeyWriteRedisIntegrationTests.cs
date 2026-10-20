using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class SpanKeyWriteRedisIntegrationTests(RedisContainerFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_span_write_reaches_redis_and_a_cold_node_reads_it_by_key()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var writer = Build();
        using var reader = Build();
        var key = $"it-{Guid.NewGuid():N}";
        var cache = writer.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis);
        var hash = writer.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.InMemoryRedis);

        (await SetBySpan(cache, key + ":v", "value")).Should().BeTrue();
        (await SetBySpan(hash, key + ":h", new Dictionary<string, string?> { ["f"] = "x" })).Should().BeTrue();

        var cold = reader.GetRequiredService<ICacheFactory>();
        (await cold.CreateCache(KnownCacheProviderNames.InMemoryRedis).GetAsync<string>(key + ":v", Ct)).Should().Be("value");
        (await cold.CreateHashCache(KnownCacheProviderNames.InMemoryRedis).GetItemAsync<string>(key + ":h", "f", Ct)).Should().Be("x");
    }

    private static ValueTask<bool> SetBySpan(ICache cache, string key, string value)
    {
        Span<char> text = key.ToCharArray();
        return cache.SetAsync<string>(text, value, TimeSpan.FromMinutes(5), policy: null, Ct);
    }

    private static ValueTask<bool> SetBySpan(IHashCache cache, string key, IDictionary<string, string?> values)
    {
        Span<char> text = key.ToCharArray();
        return cache.SetAsync<string>(text, values, TimeSpan.FromMinutes(5), policy: null, Ct);
    }

    private ServiceProvider Build() =>
        new ServiceCollection()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis();
                    b.AddInMemoryRedis(_ => { });
                },
                o => o.AppShortName = "inmemoryredis")
            .BuildServiceProvider();
}
