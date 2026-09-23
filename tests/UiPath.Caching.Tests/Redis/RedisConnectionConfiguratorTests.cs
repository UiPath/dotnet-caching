using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.Redis;

public class RedisConnectionConfiguratorTests
{

    [Fact]
    public async Task ApplyAsync_AppliesConfigurators()
    {
        var config = new ConfigurationOptions();

        await RedisConnectionConfigurators.ApplyAsync(config, [new ClientNameConfigurator("entra-test")], Provider(), TestContext.Current.CancellationToken);

        config.ClientName.Should().Be("entra-test");
    }

    [Fact]
    public async Task ApplyAsync_AppliesConfigurators_InRegistrationOrder()
    {
        var config = new ConfigurationOptions();

        await RedisConnectionConfigurators.ApplyAsync(config, [new ClientNameConfigurator("first"), new ClientNameConfigurator("second")], Provider(), TestContext.Current.CancellationToken);

        config.ClientName.Should().Be("second");
    }

    [Fact]
    public async Task ApplyAsync_WithNoConfigurators_LeavesBaseConfiguration()
    {
        var config = new ConfigurationOptions();

        await RedisConnectionConfigurators.ApplyAsync(config, [], Provider(), TestContext.Current.CancellationToken);

        config.ClientName.Should().BeNull();
    }

    [Fact]
    public async Task ApplyAsync_WithNullConfigurators_LeavesBaseConfiguration()
    {
        var config = new ConfigurationOptions();

        await RedisConnectionConfigurators.ApplyAsync(config, null, Provider(), TestContext.Current.CancellationToken);

        config.ClientName.Should().BeNull();
    }

    [Fact]
    public async Task ApplyAsync_KeepsARelaxedTimeoutAConfiguratorSet()
    {
        var optionsProvider = Provider("localhost:6379,asyncTimeout=1000");
        var config = optionsProvider.GetConfiguration();

        await RedisConnectionConfigurators.ApplyAsync(config, [new RelaxedTimeoutConfigurator(TimeSpan.FromSeconds(42))], optionsProvider, TestContext.Current.CancellationToken);

        config.ToString().Should().Contain("maintRelaxedTimeout=42", "the configurator outranks the derived bound");
    }

    [Fact]
    public async Task ApplyAsync_RederivesTheRelaxedTimeoutFromAnAsyncTimeoutAConfiguratorChanged()
    {
        var optionsProvider = Provider("localhost:6379,asyncTimeout=1000");
        var config = optionsProvider.GetConfiguration();

        await RedisConnectionConfigurators.ApplyAsync(config, [new AsyncTimeoutConfigurator(30000)], optionsProvider, TestContext.Current.CancellationToken);

        config.ToString().Should().Contain("maintRelaxedTimeout=60", "the bound follows the timeout the connection ends up with");
    }

    [Fact]
    public async Task ApplyAsync_LeavesTheRelaxedTimeoutUnsetWhenNoConfiguratorMovedTheAsyncTimeout()
    {
        // DI supplies an empty enumerable, not null.
        var optionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(new RedisConnectionOptions()));
        var config = optionsProvider.GetConfiguration();

        await RedisConnectionConfigurators.ApplyAsync(config, [], optionsProvider, TestContext.Current.CancellationToken);

        config.ToString().Should().NotContain("maintRelaxedTimeout");
    }

    [Fact]
    public async Task ApplyAsync_DerivesTheRelaxedTimeoutWhenAConfiguratorSuppliesTheAsyncTimeout()
    {
        var optionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(new RedisConnectionOptions()));
        var config = optionsProvider.GetConfiguration();

        await RedisConnectionConfigurators.ApplyAsync(config, [new AsyncTimeoutConfigurator(30000)], optionsProvider, TestContext.Current.CancellationToken);

        config.ToString().Should().Contain("maintRelaxedTimeout=60");
    }

    [Fact]
    public async Task ApplyAsync_KeepsTheUpFrontDerivationWhenNoConfiguratorMovedTheAsyncTimeout()
    {
        var optionsProvider = Provider("localhost:6379,asyncTimeout=1000");
        var config = optionsProvider.GetConfiguration();

        await RedisConnectionConfigurators.ApplyAsync(config, [new ClientNameConfigurator("noop")], optionsProvider, TestContext.Current.CancellationToken);

        config.ToString().Should().Contain("maintRelaxedTimeout=2");
    }

    [Fact]
    public async Task ApplyAsync_KeepsARelaxedTimeoutAConfiguratorSetToTheValueItWouldHaveDerived()
    {
        // Assigns exactly what the derivation would give, so only presence can tell it was set.
        var optionsProvider = Provider("localhost:6379,asyncTimeout=1000");
        var config = optionsProvider.GetConfiguration();

        await RedisConnectionConfigurators.ApplyAsync(
            config,
            [new RelaxedTimeoutConfigurator(TimeSpan.FromSeconds(2)), new AsyncTimeoutConfigurator(30000)],
            optionsProvider,
            TestContext.Current.CancellationToken);

        config.ToString().Should().Contain("maintRelaxedTimeout=2", "the configurator assigned it, whatever the value");
    }

    private static RedisConfigurationOptionsProvider Provider(string connectionString = "localhost:6379") =>
        new(NullLoggerFactory.Instance, Options.Create(new RedisConnectionOptions { ConnectionString = connectionString }));

    private sealed class RelaxedTimeoutConfigurator(TimeSpan relaxed) : IRedisConnectionConfigurator
    {
        public ValueTask ConfigureAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
        {
#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
            configuration.MaintenanceRelaxedTimeout = relaxed;
#pragma warning restore SER010
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AsyncTimeoutConfigurator(int asyncTimeout) : IRedisConnectionConfigurator
    {
        public ValueTask ConfigureAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
        {
            configuration.AsyncTimeout = asyncTimeout;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ClientNameConfigurator(string name) : IRedisConnectionConfigurator
    {
        public ValueTask ConfigureAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
        {
            configuration.ClientName = name;
            return ValueTask.CompletedTask;
        }
    }
}
