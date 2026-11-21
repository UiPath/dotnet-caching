using System.Globalization;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>
/// Probes one server of a move (section 3.8): scripts, replicas, WAIT, scan, slot enforcement, eviction policy, and every command it denies. Run it on a
/// connection of its own: the WAIT probe blocks the connection for up to 15 s on a server whose replica does not answer.
/// </summary>
internal static class SideProbe
{
    private const string WriteScript = "return redis.call('SET', KEYS[1], ARGV[1], 'PX', 60000)";
    private const string TwoKeyScript = "return redis.call('EXISTS', KEYS[1], KEYS[2])";
    private const int WaitWrites = 5;
    private const long WaitMilliseconds = 3000;
    private const int MaxScanSteps = 1000;

    /// <param name="database">The side's view: the Src prefixed view or the Dst view.</param>
    /// <param name="probePrefix">The library prefix and keyspace probe keys are written under, so they never meet application keys.</param>
    /// <param name="scanPattern">The pattern the mover scans with on this side, matched as the server stores keys.</param>
    /// <param name="slotEnforced">Set by configuration instead of probed, for a server whose policy is known.</param>
    public static async Task<SideCaps> ProbeAsync(IDatabase database, string probePrefix, string scanPattern, bool? slotEnforced = null)
    {
        var denied = new List<string>();
        var nonce = MoveScripts.NewNonce();
        var key = (RedisKey)$"{probePrefix}probe:{{{nonce}}}";
        var server = database.Multiplexer.GetServer(await database.IdentifyEndpointAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false)
            ?? throw new RedisException("The probe key has no master."));
        try
        {
            var scripts = await ScriptsAsync(database, key).ConfigureAwait(false);
            var replicas = await ReplicasAsync(server, denied).ConfigureAwait(false);
            var wait = replicas != 0 && await WaitAsync(database, server, key, denied).ConfigureAwait(false);
            var enforced = slotEnforced ?? (!scripts || await SlotEnforcedAsync(server, probePrefix, denied).ConfigureAwait(false));
            var scan = await ScanAsync(database, server, scanPattern, enforced, denied).ConfigureAwait(false);
            var eviction = await EvictionPolicyAsync(database).ConfigureAwait(false);
            return new SideCaps(server.Version, scripts, wait, scan, enforced, eviction, replicas, denied);
        }
        finally
        {
            await database.KeyDeleteAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
        }
    }

    private static bool Denies(Exception ex) =>
        ex is RedisServerException server
        && (server.Message.StartsWith("NOPERM", StringComparison.OrdinalIgnoreCase)
            || server.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            || server.Message.Contains("not allowed", StringComparison.OrdinalIgnoreCase)
            || server.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> ScriptsAsync(IDatabase database, RedisKey key)
    {
        try
        {
            await database.ScriptEvaluateAsync(WriteScript, [key], ["probe"], CommandFlags.DemandMaster).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (RedisUtils.RefusesScript(ex))
        {
            return false;
        }
    }

    private static async Task<int?> ReplicasAsync(IServer server, List<string> denied)
    {
        try
        {
            return await server.RoleAsync().ConfigureAwait(false) is Role.Master master ? master.Replicas.Count : 0;
        }
        catch (Exception ex) when (Denies(ex))
        {
            denied.Add("ROLE");
            return null;
        }
    }

    /// <summary>Five writes, each followed by <c>WAIT 1 3000</c>: any 1 or more means available; 0 or a timeout proves nothing alone.</summary>
    private static async Task<bool> WaitAsync(IDatabase database, IServer server, RedisKey key, List<string> denied)
    {
        for (var i = 0; i < WaitWrites; i++)
        {
            await database.StringSetAsync(key, i.ToString(CultureInfo.InvariantCulture), TimeSpan.FromMinutes(1), flags: CommandFlags.DemandMaster).ConfigureAwait(false);
            try
            {
                if ((long)await server.ExecuteAsync("WAIT", 1, WaitMilliseconds).ConfigureAwait(false) >= 1)
                {
                    return true;
                }
            }
            catch (Exception ex) when (Denies(ex))
            {
                denied.Add("WAIT");
                return false;
            }
            catch (RedisException)
            {
                // A timeout counts as one more write without an acknowledgement.
            }
        }

        return false;
    }

    /// <summary>A two-key script across two slots, sent to one server: CROSSSLOT means the server enforces slots.</summary>
    private static async Task<bool> SlotEnforcedAsync(IServer server, string probePrefix, List<string> denied)
    {
        var first = $"{probePrefix}probe:{{{SlotTags.For(0)}}}";
        var second = $"{probePrefix}probe:{{{SlotTags.For(8000)}}}";
        try
        {
            await server.ExecuteAsync("EVAL", TwoKeyScript, 2, first, second).ConfigureAwait(false);
            return false;
        }
        catch (RedisServerException ex) when (ex.Message.Contains("CROSSSLOT", StringComparison.OrdinalIgnoreCase) || ex.Message.StartsWith("MOVED", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        catch (Exception ex) when (Denies(ex))
        {
            denied.Add("EVAL");
            return true;
        }
    }

    /// <summary><c>SCAN 0 MATCH pattern COUNT 10 TYPE string</c> answers and its loop ends, on every primary, or on the one unit a proxy shows.</summary>
    private static async Task<ScanMode> ScanAsync(IDatabase database, IServer server, string pattern, bool enforced, List<string> denied)
    {
        var cluster = server.ServerType == ServerType.Cluster;
        var proxy = server.ServerType is ServerType.Twemproxy or ServerType.Envoyproxy || (!cluster && enforced);
        var units = proxy ? [server] : database.Multiplexer.GetServers().Where(static s => !s.IsReplica && s.IsConnected).ToList();
        try
        {
            foreach (var unit in units)
            {
                var cursor = "0";
                for (var step = 0; ; step++)
                {
                    if (step >= MaxScanSteps)
                    {
                        return ScanMode.None;
                    }

                    var reply = (RedisResult[])(await unit.ExecuteAsync("SCAN", cursor, "MATCH", pattern, "COUNT", 10, "TYPE", "string").ConfigureAwait(false))!;
                    cursor = (string)reply[0]!;
                    if (cursor == "0")
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (Denies(ex))
        {
            denied.Add("SCAN");
            return ScanMode.None;
        }
        catch (RedisServerException)
        {
            return ScanMode.None;
        }

        return proxy ? ScanMode.ProxySingleUnit : ScanMode.PerShard;
    }

    /// <summary>From <c>CONFIG GET</c> where allowed, else from <c>INFO memory</c>; null when the server tells neither (a managed service blocks CONFIG).</summary>
    private static async Task<string?> EvictionPolicyAsync(IDatabase database)
    {
        try
        {
            var reply = (RedisResult[])(await database.ExecuteAsync("CONFIG", "GET", "maxmemory-policy").ConfigureAwait(false))!;
            if (reply.Length == 2)
            {
                return (string?)reply[1];
            }
        }
        catch (Exception ex) when (ex is RedisException or RedisCommandException)
        {
            // Blocked, or admin-only on a connection without allowAdmin. INFO below.
        }

        try
        {
            var info = (string?)await database.ExecuteAsync("INFO", "memory").ConfigureAwait(false);
            var line = info?.Split('\n').FirstOrDefault(l => l.StartsWith("maxmemory_policy:", StringComparison.Ordinal));
            return line?["maxmemory_policy:".Length..].Trim();
        }
        catch (Exception ex) when (ex is RedisException or RedisCommandException)
        {
            return null;
        }
    }
}
