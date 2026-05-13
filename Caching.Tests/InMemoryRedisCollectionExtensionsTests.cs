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

    [Fact]
    public void AddInMemoryRedis_throws_at_registration_when_DistributedLockExpiry_is_zero()
    {
        var services = new ServiceCollection();
        var act = () => services.AddCaching(builder =>
            builder.AddInMemoryRedis(opt =>
            {
                opt.DistributedLockExpiry = TimeSpan.Zero;
            }));

        act.Should().Throw<OptionsValidationException>()
            .Which.OptionsType.Should().Be(typeof(InMemoryRedisCacheOptions));
    }

    [Fact]
    public void AddInMemoryRedis_throws_at_registration_when_DistributedLockTimeout_is_negative()
    {
        var services = new ServiceCollection();
        var act = () => services.AddCaching(builder =>
            builder.AddInMemoryRedis(opt =>
            {
                opt.DistributedLockTimeout = TimeSpan.FromMilliseconds(-1);
            }));

        act.Should().Throw<OptionsValidationException>()
            .Which.OptionsType.Should().Be(typeof(InMemoryRedisCacheOptions));
    }

    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddCaching(builder => builder.AddInMemoryRedis());
        return services.BuildServiceProvider();
    }
}
