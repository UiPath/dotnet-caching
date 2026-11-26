using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.Support;

// The faults of a replica-backed cluster that eng/keymove-kit.sh started, made with Redis commands only: a failover, a restart and a pause.
internal sealed class ClusterKit : IAsyncDisposable
{
    private const string PasswordSetting = "requirepass";

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    private static readonly string LostWritePassword = "lost-" + Guid.NewGuid().ToString("N");
    private SlotMigration? _migration;

    private ClusterKit(ConnectionMultiplexer mux) => Mux = mux;

    public ConnectionMultiplexer Mux { get; }

    /// <summary>Skips the test unless the integration tests run against a cluster with a replica for every master.</summary>
    public static async Task<ClusterKit> CreateAsync()
    {
        Assert.SkipUnless(string.Equals(Environment.GetEnvironmentVariable("RUN_REDIS_INTEGRATION_TESTS"), "1", StringComparison.Ordinal), "Set RUN_REDIS_INTEGRATION_TESTS=1 to run.");
        var connection = Environment.GetEnvironmentVariable("REDIS_CLUSTER_CONNECTION_STRING");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connection), "Set REDIS_CLUSTER_CONNECTION_STRING to run the cluster fault tests.");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("REDIS_CLUSTER_REPLICAS") == "1", "Start the cluster with eng/keymove-kit.sh, which gives every master a replica.");
        var mux = await ConnectionMultiplexer.ConnectAsync(connection + ",allowAdmin=true");
        return await CreateAsync(mux, static kit => kit.WaitSettledAsync());
    }

    /// <summary>Holds every client command on <paramref name="server"/> for <paramref name="span"/>.</summary>
    public static Task PauseAsync(IServer server, TimeSpan span) => server.ExecuteAsync("CLIENT", "PAUSE", ((long)span.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The master that serves the slot of <paramref name="key"/>.</summary>
    public IServer MasterOf(RedisKey key)
    {
        var slot = Mux.HashSlot(key);
        var node = Nodes().Single(n => !n.IsReplica && n.Slots.Any(r => r.From <= slot && slot <= r.To));
        return Mux.GetServer(node.EndPoint);
    }

    public IServer ReplicaOf(IServer master)
    {
        var id = Nodes().Single(n => Equals(n.EndPoint, master.EndPoint)).NodeId;
        return Mux.GetServer(Nodes().Single(n => n.IsReplica && n.ParentNodeId == id).EndPoint);
    }

    /// <summary>The replica of the master of <paramref name="key"/> takes over its slots; with <paramref name="takeover"/> it does not wait for the master to agree.</summary>
    /// <returns>The new master.</returns>
    public async Task<IServer> FailoverAsync(RedisKey key, bool takeover = false)
    {
        var replica = ReplicaOf(MasterOf(key));
        await (takeover ? replica.ExecuteAsync("CLUSTER", "FAILOVER", "TAKEOVER") : replica.ExecuteAsync("CLUSTER", "FAILOVER"));
        await UntilAsync(async () => await RoleAsync(replica) == "master" && Equals(await ServingAsync(key), replica.EndPoint));
        await WaitSettledAsync();
        return replica;
    }

    /// <summary>Stops <paramref name="server"/> with SHUTDOWN NOSAVE and waits for a new Redis process and a settled cluster.</summary>
    public async Task RestartAsync(IServer server)
    {
        var runId = await ReadRunIdAsync(server);
        try
        {
            await server.ExecuteAsync("SHUTDOWN", "NOSAVE");
        }
        catch (RedisConnectionException ex) when (ex.FailureType == ConnectionFailureType.SocketClosed)
        {
            // The server closes the connection as it stops.
        }

        await UntilAsync(async () => await ReadRunIdAsync(server) != runId);
        await WaitSettledAsync();
    }

    /// <summary>Runs <paramref name="writes"/> on the master of <paramref name="key"/> while its replica cannot follow, then lets the replica take over,
    /// so every write of that shard made in <paramref name="writes"/> is acknowledged and then lost, as in a failover that drops what it had not replicated.</summary>
    /// <returns>The new master.</returns>
    public Task<IServer> LoseWritesAsync(RedisKey key, Func<Task> writes)
    {
        var master = MasterOf(key);
        return LoseWritesAsync(master, ReplicaOf(master), writes);
    }

    /// <summary>Starts moving <paramref name="slot"/> to another master and migrates only <paramref name="keys"/> of it, so the slot is split between the two.</summary>
    /// <returns>The migration, to finish with <see cref="FinishMigrationAsync"/>.</returns>
    public async Task<SlotMigration> BeginMigrationAsync(int slot, params RedisKey[] keys)
    {
        var nodes = Nodes();
        var source = nodes.Single(n => !n.IsReplica && n.Slots.Any(r => r.From <= slot && slot <= r.To));
        var target = nodes.First(n => !n.IsReplica && n.Slots.Count > 0 && n.NodeId != source.NodeId);
        var (from, to) = (Mux.GetServer(source.EndPoint!), Mux.GetServer(target.EndPoint!));
        var migration = new SlotMigration(slot, from, to, source.NodeId, target.NodeId);
        await to.ExecuteAsync("CLUSTER", "SETSLOT", slot, "IMPORTING", source.NodeId);
        _migration = migration;
        await from.ExecuteAsync("CLUSTER", "SETSLOT", slot, "MIGRATING", target.NodeId);
        await MigrateAsync(migration, keys);
        return migration;
    }

    /// <summary>Migrates the rest of the slot and gives it to the new master on every master.</summary>
    public async Task FinishMigrationAsync(SlotMigration migration)
    {
        while (true)
        {
            var left = (RedisResult[]?)await migration.From.ExecuteAsync("CLUSTER", "GETKEYSINSLOT", migration.Slot, 100) ?? [];
            if (left.Length == 0)
            {
                break;
            }

            await MigrateAsync(migration, [.. left.Select(k => (RedisKey)(string?)k)]);
        }

        foreach (var master in Nodes().Where(n => !n.IsReplica && n.Slots.Count > 0).Select(n => Mux.GetServer(n.EndPoint!)).Append(migration.To).Distinct())
        {
            await master.ExecuteAsync("CLUSTER", "SETSLOT", migration.Slot, "NODE", migration.TargetId);
        }

        _migration = null;
        await WaitSettledAsync();
    }

    /// <summary>Waits until every master reports the cluster ok and every master has a linked replica.</summary>
    public Task WaitSettledAsync() => UntilAsync(async () =>
    {
        await Mux.ConfigureAsync();
        var nodes = Nodes();
        var masters = nodes.Where(n => !n.IsReplica && n.Slots.Count > 0).ToList();
        var replicas = nodes.Where(n => n.IsReplica).ToList();
        if (masters.Count == 0 || masters.Any(m => !replicas.Any(r => r.ParentNodeId == m.NodeId)))
        {
            return false;
        }

        foreach (var master in masters)
        {
            var info = (string?)await Mux.GetServer(master.EndPoint).ExecuteAsync("CLUSTER", "INFO");
            if (info?.Contains("cluster_state:ok", StringComparison.Ordinal) != true)
            {
                return false;
            }
        }

        foreach (var replica in replicas)
        {
            if (!replica.IsConnected || !await ReplicaIsReadyAsync(Mux.GetServer(replica.EndPoint)))
            {
                return false;
            }
        }

        return true;
    });

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_migration is { } migration)
            {
                await FinishMigrationAsync(migration);
            }
        }
        finally
        {
            await Mux.DisposeAsync();
        }
    }

    internal static async Task<ClusterKit> CreateAsync(ConnectionMultiplexer mux, Func<ClusterKit, Task> settle)
    {
        var kit = new ClusterKit(mux);
        try
        {
            await settle(kit);
            return kit;
        }
        catch
        {
            await kit.DisposeAsync();
            throw;
        }
    }

    internal static async Task<string> ReadRunIdAsync(IServer server) =>
        (await server.InfoAsync("server")).SelectMany(section => section).Single(field => field.Key == "run_id").Value;

    internal static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                // A node that is restarting or failing over answers again shortly.
            }

            if (TimeProvider.System.GetElapsedTime(started) > Settle)
            {
                throw new TimeoutException("The cluster did not settle in time.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System);
        }
    }

    internal async Task<IServer> LoseWritesAsync(IServer master, IServer replica, Func<Task> writes)
    {
        var endpoint = master.EndPoint;
        try
        {
            // Existing clients stay authenticated. Redis rejects the replica's next connection.
            await master.ConfigSetAsync(PasswordSetting, LostWritePassword);
            await master.ExecuteAsync("CLIENT", "KILL", "TYPE", "replica");
            await writes();
            await replica.ExecuteAsync("CLUSTER", "FAILOVER", "TAKEOVER");
            await UntilAsync(async () => await RoleAsync(replica) == "master");
        }
        finally
        {
            await ResetPasswordAsync(master, endpoint);
        }

        await WaitSettledAsync();

        // The old master serves reads as a replica only once it has synced from the new one and dropped what it alone held.
        await UntilAsync(async () => await RoleAsync(master) == "slave" && await ReplicaIsReadyAsync(master));
        return replica;
    }

    private static async Task ResetPasswordAsync(IServer master, EndPoint endpoint)
    {
        if (master.IsConnected)
        {
            try
            {
                await master.ConfigSetAsync(PasswordSetting, string.Empty);
                return;
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException or ObjectDisposedException)
            {
                // A reconnected client needs the temporary password.
            }
        }

        var options = new ConfigurationOptions { AllowAdmin = true, Password = LostWritePassword };
        options.EndPoints.Add(endpoint);
        await using var connection = await ConnectionMultiplexer.ConnectAsync(options);
        await connection.GetServer(endpoint).ConfigSetAsync(PasswordSetting, string.Empty);
    }

    private static async Task<bool> ReplicaIsReadyAsync(IServer server)
    {
        var info = (await server.InfoAsync("replication")).SelectMany(section => section).ToDictionary();
        return info.GetValueOrDefault("master_link_status") == "up" && info.GetValueOrDefault("master_sync_in_progress") == "0";
    }

    private static async Task<string?> RoleAsync(IServer server)
    {
        var role = await server.ExecuteAsync("ROLE");
        return ((RedisResult[]?)role)?[0].ToString();
    }

    private static async Task MigrateAsync(SlotMigration migration, RedisKey[] keys)
    {
        if (keys.Length == 0)
        {
            return;
        }

        var target = (IPEndPoint)migration.To.EndPoint;
        object[] args = ["MIGRATE", target.Address.ToString(), target.Port, string.Empty, 0, 5000, "KEYS", .. keys.Select(k => (object)k.ToString())];
        await migration.From.ExecuteAsync((string)args[0], args[1..]);
    }

    private async Task<EndPoint?> ServingAsync(RedisKey key)
    {
        await Mux.ConfigureAsync();
        var slot = Mux.HashSlot(key);
        return Nodes().SingleOrDefault(n => !n.IsReplica && n.Slots.Any(r => r.From <= slot && slot <= r.To))?.EndPoint;
    }

    private ICollection<ClusterNode> Nodes() =>
        Mux.GetServers().First(s => s.IsConnected).ClusterConfiguration?.Nodes ?? throw new InvalidOperationException("The servers are not a cluster.");
}

// A slot on its way from one master to another.
internal sealed record SlotMigration(int Slot, IServer From, IServer To, string SourceId, string TargetId);
