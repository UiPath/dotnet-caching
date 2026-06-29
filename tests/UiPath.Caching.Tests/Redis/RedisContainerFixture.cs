namespace UiPath.Caching.Tests.Redis;

public sealed class RedisContainerFixture : IAsyncLifetime
{
    public bool Enabled { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public ValueTask InitializeAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REDIS_INTEGRATION_TESTS"), "1", StringComparison.Ordinal))
        {
            return ValueTask.CompletedTask;
        }

        var connectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING");
        ConnectionString = string.IsNullOrWhiteSpace(connectionString) ? "localhost:6379" : connectionString;
        Enabled = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[CollectionDefinition("RedisIntegration")]
public sealed class RedisIntegrationCollection : ICollectionFixture<RedisContainerFixture>;
