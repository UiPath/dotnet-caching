using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.Redis;

/// <summary>Scans keys the way a cluster needs: every primary, since a key lives on the shard its slot belongs to and a scan sees one server's.</summary>
internal static class RedisTestKeys
{
    /// <summary>The connected primaries, without the entry endpoint of a cluster reached through a DNS name.</summary>
    public static IServer[] Primaries(IConnectionMultiplexer multiplexer) =>
        [.. ClusterServers.Nodes(multiplexer.GetServers()).Where(static server => !server.IsReplica && server.IsConnected)];

    public static IEnumerable<RedisKey> Keys(IConnectionMultiplexer multiplexer, string pattern) =>
        Primaries(multiplexer).SelectMany(server => server.Keys(pattern: pattern)).Distinct();

    public static async IAsyncEnumerable<RedisKey> KeysAsync(IConnectionMultiplexer multiplexer, string pattern)
    {
        foreach (var server in Primaries(multiplexer))
        {
            await foreach (var key in server.KeysAsync(pattern: pattern))
            {
                yield return key;
            }
        }
    }

    /// <summary>Runs a command on every primary, as ACL users and the script cache are per node and a cluster needs the same set-up on each.</summary>
    public static async Task<RedisResult[]> OnPrimariesAsync(IConnectionMultiplexer multiplexer, string command, params object[] arguments) =>
        await Task.WhenAll(Primaries(multiplexer).Select(server => server.ExecuteAsync(command, arguments)));

    /// <summary>The server-side calls of the commands <c>INFO commandstats</c> names with <paramref name="prefix"/>, summed over the primaries.</summary>
    public static async Task<long> CommandCallsAsync(IConnectionMultiplexer multiplexer, string prefix)
    {
        var total = 0L;
        foreach (var server in Primaries(multiplexer))
        {
            total += (await server.InfoAsync("commandstats")).SelectMany(section => section)
                .Where(stat => stat.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Sum(stat => long.Parse(stat.Value.Split(',')[0]["calls=".Length..], System.Globalization.CultureInfo.InvariantCulture));
        }

        return total;
    }

    /// <summary>The <c>PUBSUB NUMSUB</c> of a channel summed over the primaries: a subscription lives on one node and a keyless command goes to whichever one the client picks.</summary>
    public static async Task<long> SubscribersAsync(IConnectionMultiplexer multiplexer, string channel) =>
        (await OnPrimariesAsync(multiplexer, "PUBSUB", "NUMSUB", channel)).Sum(reply => (long)((RedisResult[])reply!)[1]);

    /// <summary>The subscribers of a channel summed over the primaries, by <c>PUBSUB SHARDNUMSUB</c> for a sharded channel, which a cluster subscribes to with SSUBSCRIBE and NUMSUB does not count.</summary>
    public static async Task<long> SubscribersAsync(IConnectionMultiplexer multiplexer, RedisChannel channel) =>
        (await OnPrimariesAsync(multiplexer, "PUBSUB", channel.IsSharded ? "SHARDNUMSUB" : "NUMSUB", channel.ToString())).Sum(reply => (long)((RedisResult[])reply!)[1]);

    /// <summary>The <c>PUBSUB NUMPAT</c> summed over the primaries.</summary>
    public static async Task<long> PatternsAsync(IConnectionMultiplexer multiplexer) =>
        (await OnPrimariesAsync(multiplexer, "PUBSUB", "NUMPAT")).Sum(reply => (long)reply);
}
