using StackExchange.Redis;

namespace UiPath.Caching.PluginContract.Tests;

public sealed class RedisFixture : IAsyncLifetime
{
    public bool Enabled { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("RUN_REDIS_INTEGRATION_TESTS"), "1", StringComparison.Ordinal))
        {
            var connectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING");
            ConnectionString = string.IsNullOrWhiteSpace(connectionString) ? "localhost:6379" : connectionString;
            Enabled = true;
            await using var redis = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[CollectionDefinition("RedisIntegration")]
public sealed class RedisIntegrationCollection : ICollectionFixture<RedisFixture>;
