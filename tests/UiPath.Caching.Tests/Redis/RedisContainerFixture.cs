using StackExchange.Redis;

namespace UiPath.Caching.Tests.Redis;

public sealed class RedisContainerFixture : IAsyncLifetime
{
    public const string StreamsSkipReason = "Needs the Redis Streams commands, which this server lacks or runs differently (no stream commands, or a deleted stream that keeps its groups).";

    private ConnectionMultiplexer? _clockConnection;

    public bool Enabled { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public DateTimeOffset TimeOrigin => _clockConnection is { } connection
        ? new DateTimeOffset(DateTime.SpecifyKind(connection.GetServer(connection.GetEndPoints()[0]).Time(), DateTimeKind.Utc))
        : DateTimeOffset.UnixEpoch;

    /// <summary>The version of the server the tests run against; 0.0 when they do not run.</summary>
    public Version ServerVersion { get; private set; } = new(0, 0);

    public bool SupportsAcl { get; private set; }

    /// <summary>Whether XGROUP CREATE with MKSTREAM works; Garnet has no stream commands.</summary>
    public bool SupportsStreams { get; private set; }

    /// <summary>The Kvrocks quirk: a deleted stream still has its groups, so XREADGROUP after DEL and XADD does not answer NOGROUP.</summary>
    public bool StreamsKeepGroupsAfterDelete { get; private set; }

    /// <summary>Whether the Streams commands behave as Redis's do, which the Streams-only tests need.</summary>
    public bool StreamsUsable => SupportsStreams && !StreamsKeepGroupsAfterDelete;

    /// <summary>Whether XINFO GROUPS reports the count a group was made with by XGROUP CREATE ENTRIESREAD; Dragonfly takes the option and reports nil.</summary>
    public bool ReportsEntriesRead { get; private set; }

    public bool SupportsShardedPubSub { get; private set; }

    public bool SupportsEvalRo { get; private set; }

    /// <summary>Whether INFO commandstats answers with a count for a command just sent, through a connection with allowAdmin=true.</summary>
    public bool ReportsCommandStats { get; private set; }

    /// <summary>Whether CLIENT KILL TYPE is understood.</summary>
    public bool SupportsClientKillByType { get; private set; }

    /// <summary>Whether ACL LOG answers a refused command with the entry fields (count, reason, username) a test counts refusals by.</summary>
    public bool ReportsAclLog { get; private set; }

    /// <summary>Whether the server is a cluster: a key lives on one shard, ACL users and scripts are per node, and a test that reads one server sees a part.</summary>
    public bool IsCluster { get; private set; }

    /// <summary>Whether <c>PUBSUB NUMSUB</c>, summed over the primaries, counts one for one connection subscribed; Azure Managed Redis answers a cluster-wide number that is not a client count.</summary>
    public bool CountsSubscribersPerClient { get; private set; }

    /// <summary>The server's own INFO, which says what it is: its kind is Garnet, Kvrocks or Dragonfly, or none of them.</summary>
    public string ServerKind { get; private set; } = string.Empty;

    /// <summary>Redis or Valkey, which every probe must pass on.</summary>
    public bool IsReferenceServer { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_REDIS_INTEGRATION_TESTS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING");
        ConnectionString = string.IsNullOrWhiteSpace(connectionString) ? "localhost:6379" : connectionString;
        Enabled = true;
        var connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        _clockConnection = connection;
        var server = connection.GetServer(connection.GetEndPoints()[0]);
        ServerVersion = server.Version;
        SupportsAcl = await CanSetAclUserAsync(server);
        var options = ConfigurationOptions.Parse(ConnectionString);
        options.AllowAdmin = true;
        await using var admin = await ConnectionMultiplexer.ConnectAsync(options);
        var adminServer = admin.GetServer(admin.GetEndPoints()[0]);
        var db = admin.GetDatabase();
        var scratch = $"it-probe:{Guid.NewGuid():N}";
        try
        {
            IsCluster = admin.GetServers().Any(static server => server.ServerType == ServerType.Cluster);
            CountsSubscribersPerClient = await CountsSubscribersPerClientAsync(admin);
            SupportsStreams = await TryAsync(() => db.ExecuteAsync("XGROUP", "CREATE", scratch + ":s", "g", "$", "MKSTREAM"));
            StreamsKeepGroupsAfterDelete = SupportsStreams && await KeepsGroupsAfterDeleteAsync(db, scratch + ":q");
            ReportsEntriesRead = SupportsStreams && await ReportsEntriesReadAsync(db, scratch + ":e");
            SupportsShardedPubSub = await TryAsync(() => db.ExecuteAsync("SPUBLISH", scratch + ":c", "x"));
            SupportsEvalRo = await TryAsync(() => db.ExecuteAsync("EVAL_RO", "return 1", "0"));
            SupportsClientKillByType = await TryAsync(() => adminServer.ExecuteAsync("CLIENT", "KILL", "TYPE", "pubsub", "ID", long.MaxValue));
            ReportsCommandStats = await ReportsCommandStatsAsync(admin);
            ReportsAclLog = SupportsAcl && await ReportsAclLogAsync(options, adminServer);
            var info = await adminServer.InfoAsync();
            var fields = info.SelectMany(group => group).ToList();
            ServerKind = fields.Exists(pair => pair.Key == "kvrocks_version") ? "kvrocks"
                : fields.Exists(pair => pair.Key == "dragonfly_version") ? "dragonfly"
                : fields.Exists(pair => pair.Key == "garnet_version") || fields.Exists(pair => pair.Key == "redis_version" && pair.Value.StartsWith("garnet", StringComparison.OrdinalIgnoreCase)) ? "garnet"
                : string.Empty;
            IsReferenceServer = ServerKind.Length == 0 && ServerVersion.Major >= 7;
        }
        finally
        {
            await db.KeyDeleteAsync(scratch + ":s");
            await db.KeyDeleteAsync(scratch + ":q");
            await db.KeyDeleteAsync(scratch + ":e");
        }
    }

    public ValueTask DisposeAsync() => _clockConnection?.DisposeAsync() ?? ValueTask.CompletedTask;

    internal static async Task<bool> TryAsync(Func<Task<RedisResult>> command)
    {
        try
        {
            await command();
            return true;
        }
        catch (RedisServerException ex) when (Unsupported(ex))
        {
            return false;
        }
    }

    // A server reply is a definite answer, unless the server says it is in a temporary state.
    private static bool Unsupported(RedisServerException error) =>
        !(error.Message.Contains("LOADING", StringComparison.Ordinal)
          || error.Message.Contains("BUSY", StringComparison.Ordinal)
          || error.Message.Contains("TRYAGAIN", StringComparison.Ordinal)
          || error.Message.Contains("MASTERDOWN", StringComparison.Ordinal));

    private static async Task<bool> CountsSubscribersPerClientAsync(ConnectionMultiplexer admin)
    {
        var channel = RedisChannel.Literal($"it-probe:{Guid.NewGuid():N}");
        var subscriber = admin.GetSubscriber();
        try
        {
            await subscriber.SubscribeAsync(channel, static (_, _) => { });
            var sum = 0L;
            foreach (var server in RedisTestKeys.Primaries(admin))
            {
                var reply = (RedisResult[]?)await server.ExecuteAsync("PUBSUB", "NUMSUB", channel.ToString());
                sum += (long)reply![1];
            }
            return sum == 1;
        }
        catch (RedisServerException ex) when (Unsupported(ex))
        {
            return false;
        }
        finally
        {
            await subscriber.UnsubscribeAllAsync();
        }
    }

    private static async Task<bool> KeepsGroupsAfterDeleteAsync(IDatabase db, string key)
    {
        try
        {
            await db.ExecuteAsync("XGROUP", "CREATE", key, "g", "$", "MKSTREAM");
            await db.KeyDeleteAsync(key);
            await db.ExecuteAsync("XADD", key, "*", "f", "v");
            await db.ExecuteAsync("XREADGROUP", "GROUP", "g", "c", "COUNT", "1", "STREAMS", key, ">");
            return true;
        }
        catch (RedisServerException ex) when (ex.Message.Contains("NOGROUP", StringComparison.Ordinal))
        {
            return false;
        }
        catch (RedisServerException ex) when (Unsupported(ex))
        {
            return false;
        }
    }

    private static async Task<bool> ReportsEntriesReadAsync(IDatabase db, string key)
    {
        try
        {
            await db.StreamAddAsync(key, "f", "v");
            await db.ExecuteAsync("XGROUP", "CREATE", key, "g", "$", "ENTRIESREAD", "1");
            var group = Array.Find(await db.StreamGroupInfoAsync(key), g => g.Name == "g");
            return group.EntriesRead == 1;
        }
        catch (RedisServerException ex) when (Unsupported(ex))
        {
            return false;
        }
    }

    private static async Task<bool> ReportsCommandStatsAsync(ConnectionMultiplexer admin)
    {
        try
        {
            // The count must move at once, by what was sent, on the one node that ran it. A proxy such as Azure Managed Redis reports the whole
            // cluster on every endpoint, seconds late, so a test that reads a delta right after a call would see nothing or a multiple.
            var db = admin.GetDatabase();
            await db.PingAsync();
            var before = await RedisTestKeys.CommandCallsAsync(admin, "cmdstat_get");
            for (var i = 0; i < 3; i++)
            {
                await db.StringGetAsync("it-probe:stats");
            }

            var delta = await RedisTestKeys.CommandCallsAsync(admin, "cmdstat_get") - before;
            return delta is >= 3 and < 6;
        }
        catch (RedisServerException ex) when (Unsupported(ex))
        {
            return false;
        }
    }

    private static async Task<bool> ReportsAclLogAsync(ConfigurationOptions options, IServer admin)
    {
        var user = $"it-probe-{Guid.NewGuid():N}"[..20];
        try
        {
            await admin.ExecuteAsync("ACL", "SETUSER", user, "on", ">pw", "~*", "&*", "+@all", "-ping");
            var denied = (ConfigurationOptions)options.Clone();
            denied.User = user;
            denied.Password = "pw";
            denied.AllowAdmin = false;
            denied.AbortOnConnectFail = false;
            await using var connection = await ConnectionMultiplexer.ConnectAsync(denied);
            try
            {
                await connection.GetDatabase().ExecuteAsync("PING");
            }
            catch (RedisServerException ex) when (Unsupported(ex))
            {
                // The refusal is what the log is to hold.
            }

            var log = (RedisResult[]?)await admin.ExecuteAsync("ACL", "LOG");
            return log is { Length: > 0 } && log.Any(entry =>
            {
                var fields = ((RedisResult[]?)entry)?.Select(field => field.ToString()).ToList() ?? [];
                return fields.Contains("count") && fields.Contains("reason");
            });
        }
        catch (RedisServerException ex) when (Unsupported(ex))
        {
            return false;
        }
        finally
        {
            try
            {
                await admin.ExecuteAsync("ACL", "DELUSER", user);
            }
            catch (RedisServerException ex) when (Unsupported(ex))
            {
                // The user is gone or never was.
            }
        }
    }

    private static async Task<bool> CanSetAclUserAsync(IServer server)
    {
        var probe = $"it-probe-{Guid.NewGuid():N}"[..20];
        try
        {
            await server.ExecuteAsync("ACL", "SETUSER", probe, "off");
            await server.ExecuteAsync("ACL", "DELUSER", probe);
            return true;
        }
        catch (RedisServerException ex) when (Unsupported(ex))
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
