using System.Globalization;
using System.Net;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>
/// Probes one side of a move (section 3.8): every master its view reaches, each for hash commands, scripts, a watched transaction, replicas, WAIT and
/// eviction policy; then scan, slot enforcement, and every command it denies. Run it on a connection of its own: the WAIT probe blocks the connection
/// for up to 15 s on a server whose replica does not answer.
/// </summary>
/// <remarks>Key commands run through the side's view, since the user's ACL may restrict key patterns; ROLE, WAIT, CONFIG and INFO run on the master's own server.</remarks>
internal static class SideProbe
{
    private const string ControlScript = "redis.call('HSET', KEYS[1], 'f', 'v') local all = redis.call('HGETALL', KEYS[1]) redis.call('HDEL', KEYS[1], 'f') return #all";
    private const string TwoKeyScript = "return redis.call('EXISTS', KEYS[1], KEYS[2])";
    private const int WaitWrites = 5;
    private const long WaitMilliseconds = 3000;
    private const int DefaultScanCount = 1000;

    /// <param name="database">The side's view: the Src prefixed view or the Dst view.</param>
    /// <param name="probePrefix">The library prefix and keyspace probe keys are written under, so they never meet application keys.</param>
    /// <param name="scanPattern">The pattern the mover scans with on this side, matched as the server stores keys.</param>
    /// <param name="slotEnforced">Set by configuration instead of probed, for a server whose policy is known.</param>
    /// <param name="cancellationToken">Cancels the scan, which walks the whole keyspace of every primary.</param>
    /// <param name="scanCount">The COUNT of each scan step; tests lower it to force many steps.</param>
    public static async Task<SideCaps> ProbeAsync(
        IDatabase database,
        string probePrefix,
        string scanPattern,
        bool? slotEnforced = null,
        CancellationToken cancellationToken = default,
        int scanCount = DefaultScanCount)
    {
        var denied = new List<string>();
        var targets = await MastersAsync(database, probePrefix, cancellationToken).ConfigureAwait(false);
        var masters = new List<MasterCaps>();
        foreach (var (endpoint, key) in targets)
        {
            masters.Add(await MasterAsync(database, database.Multiplexer.GetServer(endpoint), key, denied).ConfigureAwait(false));
        }

        var first = database.Multiplexer.GetServer(targets[0].EndPoint);
        var enforced = slotEnforced ?? (!masters.TrueForAll(static m => m.Scripts) || await SlotEnforcedAsync(first, probePrefix, denied).ConfigureAwait(false));
        var scan = await ScanAsync(database, first, scanPattern, enforced, denied, scanCount, cancellationToken).ConfigureAwait(false);
        return new SideCaps(masters, scan, enforced, denied);
    }

    /// <summary>The primaries the view reaches, each with a probe key it owns: slot tags in order until every connected primary is covered or the slots run out.</summary>
    /// <remarks>A prefix that pins every key to one slot reaches one master, which is right: all keys live there. A server that is not a cluster is covered by one key.</remarks>
    private static async Task<List<(EndPoint EndPoint, RedisKey Key)>> MastersAsync(IDatabase database, string probePrefix, CancellationToken cancellationToken)
    {
        var connected = database.Multiplexer.GetServers().Where(static s => !s.IsReplica && s.IsConnected).Select(static s => s.EndPoint).ToList();
        var found = new List<(EndPoint EndPoint, RedisKey Key)>();
        for (var slot = 0; slot < HashSlot.Count; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RedisKey key = $"{probePrefix}probe:{{{SlotTags.For(slot)}}}:ctl";
            var endpoint = await database.IdentifyEndpointAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
            if (endpoint is not null && !found.Exists(f => Equals(f.EndPoint, endpoint)))
            {
                found.Add((endpoint, key));
            }

            if (found.Count > 0 && (database.Multiplexer.GetServer(found[0].EndPoint).ServerType != ServerType.Cluster || connected.TrueForAll(c => found.Exists(f => Equals(f.EndPoint, c)))))
            {
                break;
            }
        }

        return found.Count > 0 ? found : throw new RedisException("The probe keys have no master.");
    }

    private static async Task<MasterCaps> MasterAsync(IDatabase database, IServer server, RedisKey key, List<string> denied)
    {
        try
        {
            var hashes = await HashesAsync(database, key, denied).ConfigureAwait(false);
            var scripts = await ScriptsAsync(database, key, denied).ConfigureAwait(false);
            var transactions = await TransactionsAsync(database, key, denied).ConfigureAwait(false);
            var replicas = await ReplicasAsync(server, denied).ConfigureAwait(false);
            var wait = replicas != 0 && await WaitAsync(database, server, key, denied).ConfigureAwait(false);
            var eviction = await EvictionPolicyAsync(server).ConfigureAwait(false);
            return new MasterCaps(server.EndPoint, server.Version, scripts, transactions, hashes, wait, replicas, eviction);
        }
        finally
        {
            try
            {
                await database.KeyDeleteAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
            }
            catch (RedisServerException)
            {
                // Best effort: a user denied DEL leaves a key that expires or is empty already.
            }
        }
    }

    private static bool Denies(Exception ex) =>
        ex is RedisServerException server
        && (server.Message.StartsWith("NOPERM", StringComparison.OrdinalIgnoreCase)
            || server.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            || server.Message.Contains("not allowed", StringComparison.OrdinalIgnoreCase)
            || server.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase));

    /// <summary>HSET, HGETALL and HDEL on the control-like key, outside a script.</summary>
    private static async Task<bool> HashesAsync(IDatabase database, RedisKey key, List<string> denied)
    {
        try
        {
            await database.HashSetAsync(key, "f", "v", flags: CommandFlags.DemandMaster).ConfigureAwait(false);
            await database.HashGetAllAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
            await database.HashDeleteAsync(key, "f", CommandFlags.DemandMaster).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (Denies(ex) || RedisUtils.RefusesScript(ex))
        {
            denied.Add("HASH");
            return false;
        }
    }

    /// <summary>One script of the same three commands; a refusal of EVAL itself differs from a denial of a command inside the script.</summary>
    private static async Task<bool> ScriptsAsync(IDatabase database, RedisKey key, List<string> denied)
    {
        try
        {
            await database.ScriptEvaluateAsync(ControlScript, [key], flags: CommandFlags.DemandMaster).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (RedisUtils.RefusesScript(ex))
        {
            denied.Add(InsideScript(ex.Message) ? $"script:{ex.Message.Split('\n')[0].Trim()}" : "EVAL");
            return false;
        }
    }

    private static bool InsideScript(string message) =>
        message.Contains("Error running script", StringComparison.OrdinalIgnoreCase)
        || message.Contains("user_script", StringComparison.OrdinalIgnoreCase)
        || message.Contains("ACL failure in script", StringComparison.OrdinalIgnoreCase);

    /// <summary>A watched transaction: a hash length condition, then HSET and HDEL; it must execute.</summary>
    private static async Task<bool> TransactionsAsync(IDatabase database, RedisKey key, List<string> denied)
    {
        Task? queued = null;
        try
        {
            var transaction = database.CreateTransaction();
            transaction.AddCondition(Condition.HashLengthEqual(key, 0));
            queued = Task.WhenAll(transaction.HashSetAsync(key, "f", "v"), transaction.HashDeleteAsync(key, "f"));
            return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (Exception ex) when (Denies(ex) || (ex is RedisServerException && ex.Message.Contains("EXEC without MULTI", StringComparison.OrdinalIgnoreCase)))
        {
            // A server that refuses MULTI answers the EXEC that follows with this error.
            denied.Add("MULTI/WATCH");
            return false;
        }
        finally
        {
            if (queued is not null)
            {
                try
                {
                    await queued.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Observed so a denied queue does not surface later; the execute result decides.
                }
            }
        }
    }

    private static async Task<int?> ReplicasAsync(IServer server, List<string> denied)
    {
        try
        {
            return await server.RoleAsync().ConfigureAwait(false) is Role.Master master ? master.Replicas.Count : null;
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

    /// <summary><c>SCAN 0 MATCH pattern COUNT n TYPE string</c> answers and its loop ends, on every primary, or on the one unit a proxy shows; the walk is whole and cancellable.</summary>
    private static async Task<ScanMode> ScanAsync(IDatabase database, IServer server, string pattern, bool enforced, List<string> denied, int count, CancellationToken cancellationToken)
    {
        var cluster = server.ServerType == ServerType.Cluster;
        var proxy = server.ServerType is ServerType.Twemproxy or ServerType.Envoyproxy || (!cluster && enforced);
        var units = proxy ? [server] : database.Multiplexer.GetServers().Where(static s => !s.IsReplica && s.IsConnected).ToList();
        try
        {
            foreach (var unit in units)
            {
                var cursor = "0";
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var reply = (RedisResult[])(await unit.ExecuteAsync("SCAN", cursor, "MATCH", pattern, "COUNT", count, "TYPE", "string").ConfigureAwait(false))!;
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

    /// <summary>From <c>CONFIG GET</c> on the master where allowed, else from <c>INFO memory</c>; null when the server tells neither (a managed service blocks CONFIG).</summary>
    private static async Task<string?> EvictionPolicyAsync(IServer server)
    {
        try
        {
            var reply = (RedisResult[])(await server.ExecuteAsync("CONFIG", "GET", "maxmemory-policy").ConfigureAwait(false))!;
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
            var info = (string?)await server.ExecuteAsync("INFO", "memory").ConfigureAwait(false);
            var line = info?.Split('\n').FirstOrDefault(l => l.StartsWith("maxmemory_policy:", StringComparison.Ordinal));
            return line?["maxmemory_policy:".Length..].Trim();
        }
        catch (Exception ex) when (ex is RedisException or RedisCommandException)
        {
            return null;
        }
    }
}
