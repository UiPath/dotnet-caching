using StackExchange.Redis;

namespace UiPath.Caching.Tests.Redis;

public sealed class RedisContainerFixture : IAsyncLifetime
{
    public bool Enabled { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The version of the server the tests run against; 0.0 when they do not run.</summary>
    public Version ServerVersion { get; private set; } = new(0, 0);

    public async ValueTask InitializeAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REDIS_INTEGRATION_TESTS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING");
        ConnectionString = string.IsNullOrWhiteSpace(connectionString) ? "localhost:6379" : connectionString;
        Enabled = true;
        await using var connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        ServerVersion = connection.GetServer(connection.GetEndPoints()[0]).Version;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[CollectionDefinition("RedisIntegration")]
public sealed class RedisIntegrationCollection : ICollectionFixture<RedisContainerFixture>;
