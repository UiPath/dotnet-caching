using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.Redis;

public class RedisConfigurationOptionsProviderFactoryTests
{
    private sealed class FakeConfigurationOptionsProvider : IRedisConfigurationOptionsProvider
    {
        public ConfigurationOptions GetConfiguration() => new();
    }

    [Fact]
    public void Factory_provider_wins_when_registered_after_AddRedisConnection()
    {
        var custom = new FakeConfigurationOptionsProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder
            .AddRedisConnection(opt => opt.ConnectionString = "localhost:6379")
            .AddRedisConfigurationOptionsProvider(_ => custom));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRedisConfigurationOptionsProvider>().Should().BeSameAs(custom);
    }

    [Fact]
    public void Factory_provider_wins_when_registered_before_AddRedisConnection()
    {
        var custom = new FakeConfigurationOptionsProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder
            .AddRedisConfigurationOptionsProvider(_ => custom)
            .AddRedisConnection(opt => opt.ConnectionString = "localhost:6379"));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRedisConfigurationOptionsProvider>().Should().BeSameAs(custom);
    }

    [Fact]
    public void Default_provider_used_when_no_factory_registered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddRedisConnection(opt => opt.ConnectionString = "localhost:6379"));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRedisConfigurationOptionsProvider>().Should().BeOfType<RedisConfigurationOptionsProvider>();
    }

    [Fact]
    public void Null_factory_throws()
    {
        var services = new ServiceCollection();

        var act = () => services.AddCaching(builder => builder.AddRedisConfigurationOptionsProvider(null!));

        act.Should().Throw<ArgumentNullException>();
    }
}
