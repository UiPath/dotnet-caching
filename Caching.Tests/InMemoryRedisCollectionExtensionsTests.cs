using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Platform.Caching.Broadcast;
using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests;

public class InMemoryRedisCollectionExtensionsTests
{
    [Fact]
    public void Multiple_AddInMemoryRedis_in_same_process_each_register_real_change_token_factory()
    {
        using var providerA = BuildContainer();
        using var providerB = BuildContainer();

        providerA.GetRequiredService<IChangeTokenFactory>()
            .Should().BeOfType<ChangeTokenFactory<RedisValue>>();
        providerB.GetRequiredService<IChangeTokenFactory>()
            .Should().BeOfType<ChangeTokenFactory<RedisValue>>();
    }

    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddCaching(builder => builder.AddInMemoryRedis());
        return services.BuildServiceProvider();
    }
}
