using StackExchange.Redis;

namespace UiPath.Caching.Tests.Redis;

public sealed class RedisContainerFixture : IAsyncLifetime
{
    public bool Enabled { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The version of the server the tests run against; 0.0 when they do not run.</summary>
    public Version ServerVersion { get; private set; } = new(0, 0);

    public bool SupportsAcl { get; private set; }

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
        var server = connection.GetServer(connection.GetEndPoints()[0]);
        ServerVersion = server.Version;
        SupportsAcl = await CanSetAclUserAsync(server);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<bool> CanSetAclUserAsync(IServer server)
    {
        var probe = $"it-probe-{Guid.NewGuid():N}"[..20];
        try
        {
            await server.ExecuteAsync("ACL", "SETUSER", probe, "off");
            await server.ExecuteAsync("ACL", "DELUSER", probe);
            return true;
        }
        catch (RedisServerException)
        {
            return false;
        }
    }
}

[CollectionDefinition("RedisIntegration")]
public sealed class RedisIntegrationCollection : ICollectionFixture<RedisContainerFixture>;

/// <summary>Redis tests that measure allocated bytes, run alone as <see cref="AllocationCollection"/> does, with the same fixture.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RedisAllocationCollection : ICollectionFixture<RedisContainerFixture>
{
    public const string Name = "RedisAllocation";
}
