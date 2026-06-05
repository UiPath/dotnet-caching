using Microsoft.Extensions.DependencyInjection;
using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests;

public class SetCacheCollectionExtensionsTests
{
    [Fact]
    public void AddRedisSetCache_with_ResilienceKeyName_propagates_to_RedisSetCacheOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddRedisSetCache(o => o.ResilienceKeyName = "set-pop"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RedisSetCacheOptions>>().Value;
        options.ResilienceKeyName.Should().Be("set-pop");
    }

    [Fact]
    public void AddRedisSetCache_without_configuration_leaves_ResilienceKeyName_null()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddRedisSetCache());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RedisSetCacheOptions>>().Value;
        options.ResilienceKeyName.Should().BeNull();
    }

    [Fact]
    public void AddRedisSetCache_on_service_collection_propagates_ResilienceKeyName()
    {
        var services = new ServiceCollection();
        services.AddRedisSetCache(o => o.ResilienceKeyName = "set-pop");
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RedisSetCacheOptions>>().Value;
        options.ResilienceKeyName.Should().Be("set-pop");
    }
}
