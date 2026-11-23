using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// A test that stalls its replica expects WAIT to block; with a second replica attached to the same master, WAIT 1 is answered by that one.
internal static class ReplicaGuard
{
    /// <summary>Skips the test unless the master has exactly one connected replica and it is the one at <paramref name="replicaConnection"/>.</summary>
    public static void RequireOnlyReplica(string masterConnection, string replicaConnection)
    {
        var (count, ports) = Replicas(masterConnection);
        var expected = ConfigurationOptions.Parse(replicaConnection).EndPoints[0] switch
        {
            IPEndPoint ip => ip.Port,
            DnsEndPoint dns => dns.Port,
            _ => 0,
        };
        Assert.SkipUnless(count == 1 && ports.Contains(expected), $"needs exactly one replica of the master, found {count}; the replica at REDIS_REPLICA_CONNECTION_STRING must be the only one.");
    }

    private static (int Count, List<int> Ports) Replicas(string masterConnection)
    {
        try
        {
            using var mux = ConnectionMultiplexer.Connect(masterConnection + ",allowAdmin=true");
            var info = mux.GetServer(mux.GetEndPoints()[0]).Info("replication").SelectMany(g => g).ToDictionary(p => p.Key, p => p.Value);
            var count = info.TryGetValue("connected_slaves", out var n) ? int.Parse(n, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var ports = new List<int>();
            for (var i = 0; i < count; i++)
            {
                var line = info.GetValueOrDefault($"slave{i}") ?? string.Empty;
                ports.Add(line.Split(',').Select(f => f.Split('=')).Where(f => f.Length == 2 && f[0] == "port").Select(f => int.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault());
            }

            return (count, ports);
        }
        catch (RedisException ex)
        {
            Assert.Skip($"needs exactly one replica of the master, which could not be read: {ex.Message}");
            return (0, []);
        }
    }
}
