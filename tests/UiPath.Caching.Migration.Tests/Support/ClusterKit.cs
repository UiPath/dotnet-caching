using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.Support;

// The faults of a replica-backed cluster that eng/keymove-kit.sh started, made with Redis commands only: a failover, a restart and a pause.
internal sealed class ClusterKit : IAsyncDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    private ClusterKit(ConnectionMultiplexer mux) => Mux = mux;

    public ConnectionMultiplexer Mux { get; }

    /// <summary>Skips the test unless the integration tests run against a cluster with a replica for every master.</summary>
    public static async Task<ClusterKit> CreateAsync()
    {
        Assert.SkipUnless(string.Equals(Environment.GetEnvironmentVariable("RUN_REDIS_INTEGRATION_TESTS"), "1", StringComparison.Ordinal), "Set RUN_REDIS_INTEGRATION_TESTS=1 to run.");
        var connection = Environment.GetEnvironmentVariable("REDIS_CLUSTER_CONNECTION_STRING");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connection), "Set REDIS_CLUSTER_CONNECTION_STRING to run the cluster fault tests.");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("REDIS_CLUSTER_REPLICAS") == "1", "Start the cluster with eng/keymove-kit.sh, which gives every master a replica.");
        var kit = new ClusterKit(await ConnectionMultiplexer.ConnectAsync(connection + ",allowAdmin=true"));
        await kit.WaitSettledAsync();
        return kit;
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

    /// <summary>Stops <paramref name="server"/> with SHUTDOWN NOSAVE; its container restarts it empty, and the cluster is waited for until it is ok again.</summary>
    public async Task RestartAsync(IServer server)
    {
        try
        {
            await server.ExecuteAsync("SHUTDOWN", "NOSAVE");
        }
        catch (RedisException)
        {
            // The server closes the connection as it stops.
        }

        await UntilAsync(async () =>
        {
            try
            {
                await server.PingAsync();
                return true;
            }
            catch (RedisException)
            {
                return false;
            }
        });
        await WaitSettledAsync();
    }

    /// <summary>Waits until every master reports the cluster ok and every master has a linked replica.</summary>
    public Task WaitSettledAsync() => UntilAsync(async () =>
    {
        await Mux.ConfigureAsync();
        var nodes = Nodes();
        var masters = nodes.Where(n => !n.IsReplica && n.Slots.Count > 0).ToList();
        if (masters.Count == 0 || masters.Any(m => !nodes.Any(r => r.IsReplica && r.ParentNodeId == m.NodeId && r.IsConnected)))
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

        return true;
    });

    public ValueTask DisposeAsync() => Mux.DisposeAsync();

    private static async Task<string?> RoleAsync(IServer server)
    {
        var role = await server.ExecuteAsync("ROLE");
        return ((RedisResult[]?)role)?[0].ToString();
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
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
            catch (RedisException)
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

    private async Task<EndPoint?> ServingAsync(RedisKey key)
    {
        await Mux.ConfigureAsync();
        var slot = Mux.HashSlot(key);
        return Nodes().SingleOrDefault(n => !n.IsReplica && n.Slots.Any(r => r.From <= slot && slot <= r.To))?.EndPoint;
    }

    private ICollection<ClusterNode> Nodes() =>
        Mux.GetServers().First(s => s.IsConnected).ClusterConfiguration?.Nodes ?? throw new InvalidOperationException("The servers are not a cluster.");
}
