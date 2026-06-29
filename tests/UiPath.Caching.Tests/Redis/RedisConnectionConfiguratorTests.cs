using StackExchange.Redis;

namespace UiPath.Caching.Tests.Redis;

public class RedisConnectionConfiguratorTests
{
    private sealed class ClientNameConfigurator(string name) : IRedisConnectionConfigurator
    {
        public ValueTask ConfigureAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
        {
            configuration.ClientName = name;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ApplyAsync_AppliesConfigurators()
    {
        var config = new ConfigurationOptions();

        await RedisConnectionConfigurators.ApplyAsync(config, [new ClientNameConfigurator("entra-test")], TestContext.Current.CancellationToken);

        config.ClientName.Should().Be("entra-test");
    }

    [Fact]
    public async Task ApplyAsync_AppliesConfigurators_InRegistrationOrder()
    {
        var config = new ConfigurationOptions();

        await RedisConnectionConfigurators.ApplyAsync(config, [new ClientNameConfigurator("first"), new ClientNameConfigurator("second")], TestContext.Current.CancellationToken);

        config.ClientName.Should().Be("second");
    }

    [Fact]
    public async Task ApplyAsync_WithNoConfigurators_LeavesBaseConfiguration()
    {
        var config = new ConfigurationOptions();

        await RedisConnectionConfigurators.ApplyAsync(config, [], TestContext.Current.CancellationToken);

        config.ClientName.Should().BeNull();
    }

    [Fact]
    public async Task ApplyAsync_WithNullConfigurators_LeavesBaseConfiguration()
    {
        var config = new ConfigurationOptions();

        await RedisConnectionConfigurators.ApplyAsync(config, null, TestContext.Current.CancellationToken);

        config.ClientName.Should().BeNull();
    }
}
