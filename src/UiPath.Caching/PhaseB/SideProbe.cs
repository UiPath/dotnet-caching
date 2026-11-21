using System.Diagnostics;
using System.Globalization;
using System.Net;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>Probes one side of a move (section 3.8): every master its view reaches, then scan, slot enforcement, and every command it denies.</summary>
/// <remarks>
/// Each master is probed for hash commands, scripts, a watched transaction, replicas, WAIT and eviction policy. The script and the transaction run every
/// command the move uses on disposable keys that carry a per-call nonce. Run it on a connection of its own: the WAIT probe blocks the connection for up
/// to 15 s on a server whose replica does not answer. Key commands run through the side's view, since the user's ACL may restrict key patterns; ROLE,
/// WAIT, CONFIG and INFO run on the master's own server.
/// </remarks>
internal static class SideProbe
{
    // SET, GET, EXISTS, PEXPIRE, PEXPIREAT, PERSIST, RENAME, TIME, HSET, HSETNX, HGETALL, HKEYS, HLEN and HDEL: every command the move uses.
    private const string ProbeScript =
        "local t = redis.call('TIME') "
        + "redis.call('SET', KEYS[1], 'v') redis.call('GET', KEYS[1]) redis.call('EXISTS', KEYS[1]) "
        + "redis.call('PEXPIRE', KEYS[1], 60001) redis.call('PEXPIREAT', KEYS[1], (tonumber(t[1]) + 60) * 1000 + 1) redis.call('PERSIST', KEYS[1]) "
        + "redis.call('RENAME', KEYS[1], KEYS[2]) redis.call('PEXPIRE', KEYS[2], 60001) "
        + "redis.call('HSET', KEYS[1], 'f', 'v') redis.call('HSETNX', KEYS[1], 'g', 'v') redis.call('HGETALL', KEYS[1]) redis.call('HKEYS', KEYS[1]) "
        + "redis.call('HLEN', KEYS[1]) redis.call('HDEL', KEYS[1], 'f', 'g') return 1";

    private const string TwoKeyScript = "return redis.call('EXISTS', KEYS[1], KEYS[2])";
    private const int WaitWrites = 5;
    private const long WaitMilliseconds = 3000;
    private const int DefaultScanCount = 1000;
    private static readonly TimeSpan DefaultScanBudget = TimeSpan.FromSeconds(30);

    /// <param name="database">The side's view: the Src prefixed view or the Dst view.</param>
    /// <param name="probePrefix">The library prefix and keyspace probe keys are written under, so they never meet application keys.</param>
    /// <param name="scanPattern">The pattern the mover scans with on this side, matched as the server stores keys.</param>
    /// <param name="slotEnforced">Set by configuration instead of probed, for a server whose policy is known.</param>
    /// <param name="cancellationToken">Cancels the probe, including the scan, which can walk the whole keyspace of every primary.</param>
    /// <param name="scanCount">The COUNT of each scan step; tests lower it to force many steps.</param>
    public static Task<SideCaps> ProbeAsync(
        IDatabase database,
        string probePrefix,
        string scanPattern,
        bool? slotEnforced = null,
        CancellationToken cancellationToken = default,
        int scanCount = DefaultScanCount) =>
        ProbeAsync(database, probePrefix, scanPattern, DefaultScanBudget, slotEnforced, cancellationToken, scanCount);

    /// <param name="database">The side's view: the Src prefixed view or the Dst view.</param>
    /// <param name="probePrefix">The library prefix and keyspace probe keys are written under, so they never meet application keys.</param>
    /// <param name="scanPattern">The pattern the mover scans with on this side, matched as the server stores keys.</param>
    /// <param name="scanBudget">The time the scan may take, 30 s by default of the other overload; null is a full traversal. The scan is then <see cref="ScanMode.Incomplete"/>.</param>
    /// <param name="slotEnforced">Set by configuration instead of probed, for a server whose policy is known.</param>
    /// <param name="cancellationToken">Cancels the probe, including the scan, which can walk the whole keyspace of every primary.</param>
    /// <param name="scanCount">The COUNT of each scan step; tests lower it to force many steps.</param>
    public static async Task<SideCaps> ProbeAsync(
        IDatabase database,
        string probePrefix,
        string scanPattern,
        TimeSpan? scanBudget,
        bool? slotEnforced = null,
        CancellationToken cancellationToken = default,
        int scanCount = DefaultScanCount)
    {
        var nonce = MoveScripts.NewNonce();
        var denied = new List<string>();
        var (targets, unreachable) = await MastersAsync(database, probePrefix, nonce, cancellationToken).ConfigureAwait(false);
        var masters = new List<MasterCaps>();
        foreach (var (endpoint, key) in targets)
        {
            masters.Add(await MasterAsync(database, database.Multiplexer.GetServer(endpoint), key, denied).ConfigureAwait(false));
        }

        var first = database.Multiplexer.GetServer(targets[0].EndPoint);
        var enforced = slotEnforced ?? (!masters.TrueForAll(static m => m.Scripts) || await SlotEnforcedAsync(first, probePrefix, nonce, denied).ConfigureAwait(false));
        var scan = await ScanAsync(database, first, scanPattern, enforced, denied, scanCount, scanBudget, cancellationToken).ConfigureAwait(false);
        return new SideCaps(masters, scan, enforced, denied, unreachable);
    }

    /// <summary>The connected primaries the view reaches, each with a probe key base it owns, and the primaries that are not connected.</summary>
    /// <remarks>
    /// Slot tags in order until every connected primary is covered or the slots run out. A prefix that pins every key to one slot reaches one master, which
    /// is right: all keys live there. A server that is not a cluster is covered by one key. A primary that is listed but not connected is never probed or
    /// dropped: it is returned as unreachable.
    /// </remarks>
    private static async Task<(List<(EndPoint EndPoint, string Key)> Masters, List<EndPoint> Unreachable)> MastersAsync(IDatabase database, string probePrefix, string nonce, CancellationToken cancellationToken)
    {
        var primaries = database.Multiplexer.GetServers().Where(static s => !s.IsReplica).ToList();
        var unreachable = primaries.Where(static s => !s.IsConnected).Select(static s => s.EndPoint).ToList();
        var connected = primaries.Where(static s => s.IsConnected).Select(static s => s.EndPoint).ToList();
        var found = new List<(EndPoint EndPoint, string Key)>();
        for (var slot = 0; slot < HashSlot.Count; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"{probePrefix}probe:{{{SlotTags.For(slot)}}}:{nonce}";
            var endpoint = await database.IdentifyEndpointAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
            if (endpoint is not null && !unreachable.Contains(endpoint) && !found.Exists(f => Equals(f.EndPoint, endpoint)))
            {
                found.Add((endpoint, key));
            }

            if (found.Count > 0 && (database.Multiplexer.GetServer(found[0].EndPoint).ServerType != ServerType.Cluster || connected.TrueForAll(c => found.Exists(f => Equals(f.EndPoint, c)))))
            {
                break;
            }
        }

        return found.Count > 0 ? (found, unreachable) : throw new RedisException($"The probe keys have no reachable master (unreachable: {string.Join(", ", unreachable)}).");
    }

    private static async Task<MasterCaps> MasterAsync(IDatabase database, IServer server, string key, List<string> denied)
    {
        RedisKey first = $"{key}:a";
        RedisKey second = $"{key}:b";
        try
        {
            var hashes = await HashesAsync(database, server.EndPoint, first, denied).ConfigureAwait(false);
            await CleanAsync(database, first, second).ConfigureAwait(false);
            var scripts = await ScriptsAsync(database, server.EndPoint, first, second, denied).ConfigureAwait(false);
            await CleanAsync(database, first, second).ConfigureAwait(false);
            var transactions = await TransactionsAsync(database, server.EndPoint, first, second, denied).ConfigureAwait(false);
            await CleanAsync(database, first, second).ConfigureAwait(false);
            var replicas = await ReplicasAsync(server, denied).ConfigureAwait(false);
            var wait = replicas != 0 && await WaitAsync(database, server, first, denied).ConfigureAwait(false);
            var eviction = await EvictionPolicyAsync(server).ConfigureAwait(false);
            return new MasterCaps(server.EndPoint, server.Version, scripts, transactions, hashes, wait, replicas, eviction);
        }
        finally
        {
            await CleanAsync(database, first, second).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes the probe keys, so a script that stopped halfway on a denial does not leave a key of the wrong type for the next probe.</summary>
    private static async Task CleanAsync(IDatabase database, RedisKey first, RedisKey second)
    {
        try
        {
            await database.KeyDeleteAsync([first, second], CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (RedisServerException)
        {
            // Best effort: a user denied DEL leaves keys that expire or are empty already.
        }
    }

    private static bool Denies(Exception ex) =>
        ex is RedisServerException server
        && (server.Message.Contains("NOPERM", StringComparison.OrdinalIgnoreCase)
            || server.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            || server.Message.Contains("ACL failure", StringComparison.OrdinalIgnoreCase));

    private static string Denial(string command, EndPoint endpoint) => $"{command}@{endpoint}";

    /// <summary>The command a denial names (<c>... to run the 'hdel' command</c>); null when the message does not name one.</summary>
    private static string? DeniedCommand(string message)
    {
        const string marker = "run the '";
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = message.IndexOf('\'', start);
        return end > start ? message[start..end] : null;
    }

    /// <summary>HSET, HSETNX, HGETALL, HKEYS, HLEN and HDEL on the control-like key, outside a script.</summary>
    private static async Task<bool> HashesAsync(IDatabase database, EndPoint endpoint, RedisKey key, List<string> denied)
    {
        try
        {
            await database.HashSetAsync(key, "f", "v", flags: CommandFlags.DemandMaster).ConfigureAwait(false);
            await database.HashSetAsync(key, "g", "v", When.NotExists, CommandFlags.DemandMaster).ConfigureAwait(false);
            await database.HashGetAllAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
            await database.HashKeysAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
            await database.HashLengthAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
            await database.HashDeleteAsync(key, ["f", "g"], CommandFlags.DemandMaster).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (Denies(ex) || RedisUtils.RefusesScript(ex))
        {
            denied.Add(Denial("HASH", endpoint));
            return false;
        }
    }

    /// <summary>One script of every command the move uses; a refusal of EVAL itself differs from a denial of a command inside the script.</summary>
    private static async Task<bool> ScriptsAsync(IDatabase database, EndPoint endpoint, RedisKey first, RedisKey second, List<string> denied)
    {
        try
        {
            await database.ScriptEvaluateAsync(ProbeScript, [first, second], flags: CommandFlags.DemandMaster).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (RedisUtils.RefusesScript(ex))
        {
            denied.Add(Denial(InsideScript(ex.Message) ? $"script:{DeniedCommand(ex.Message) ?? ex.Message.Split('\n')[0].Trim()}" : "EVAL", endpoint));
            return false;
        }
    }

    private static bool InsideScript(string message) =>
        message.Contains("Error running script", StringComparison.OrdinalIgnoreCase)
        || message.Contains("user_script", StringComparison.OrdinalIgnoreCase)
        || message.Contains("ACL failure in script", StringComparison.OrdinalIgnoreCase);

    /// <summary>A watched transaction: a hash length condition, then every command the move uses; it must execute.</summary>
    private static async Task<bool> TransactionsAsync(IDatabase database, EndPoint endpoint, RedisKey first, RedisKey second, List<string> denied)
    {
        List<Task> queued = [];
        try
        {
            var transaction = database.CreateTransaction();
            transaction.AddCondition(Condition.HashLengthEqual(first, 0));
            var atSecond = DateTime.UtcNow.AddMinutes(1);
            Task[] commands =
            [
                transaction.StringSetAsync(first, "v"),
                transaction.StringGetAsync(first),
                transaction.KeyExistsAsync(first),
                transaction.KeyExpireAsync(first, TimeSpan.FromMilliseconds(60001)),
                transaction.KeyExpireAsync(first, new DateTime(atSecond.Ticks - (atSecond.Ticks % TimeSpan.TicksPerSecond) + (TimeSpan.TicksPerMillisecond * 500), DateTimeKind.Utc)),
                transaction.KeyPersistAsync(first),
                transaction.KeyRenameAsync(first, second),
                transaction.KeyExpireAsync(second, TimeSpan.FromMilliseconds(60001)),
                transaction.ExecuteAsync("TIME"),
                transaction.HashSetAsync(first, "f", "v"),
                transaction.HashSetAsync(first, "g", "v", When.NotExists),
                transaction.HashGetAllAsync(first),
                transaction.HashKeysAsync(first),
                transaction.HashLengthAsync(first),
                transaction.HashDeleteAsync(first, ["f", "g"]),
            ];
            queued.AddRange(commands);
            var executed = await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
            if (!executed && await QueuedDenialAsync(queued).ConfigureAwait(false) is { } command)
            {
                denied.Add(Denial(command, endpoint));
            }

            return executed;
        }
        catch (Exception ex) when (Denies(ex) || (ex is RedisServerException && (ex.Message.Contains("EXEC without MULTI", StringComparison.OrdinalIgnoreCase) || ex.Message.StartsWith("EXECABORT", StringComparison.OrdinalIgnoreCase))))
        {
            // A server that refuses MULTI answers the EXEC that follows with this error; a refused queued command aborts the EXEC.
            denied.Add(Denial(await QueuedDenialAsync(queued).ConfigureAwait(false) ?? "MULTI/WATCH", endpoint));
            return false;
        }
    }

    /// <summary>The queued command the server denied, as <c>transaction:command</c>; unnamed when the server only aborted the EXEC; null when no queued task failed so. Every queued task is observed here.</summary>
    private static async Task<string?> QueuedDenialAsync(List<Task> queued)
    {
        string? found = null;
        foreach (var task in queued)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (Denies(ex) && DeniedCommand(ex.Message) is { } command && command is not ("multi" or "exec" or "watch"))
            {
                found = $"transaction:{command}";
            }
            catch (RedisServerException ex) when (ex.Message.StartsWith("EXECABORT", StringComparison.OrdinalIgnoreCase))
            {
                // The server aborted the EXEC after it refused a queued command, and does not say which.
                found ??= "transaction:a queued command";
            }
            catch (Exception)
            {
                // Observed so a denied queue does not surface later; the execute result decides.
            }
        }

        return found;
    }

    private static async Task<int?> ReplicasAsync(IServer server, List<string> denied)
    {
        try
        {
            return await server.RoleAsync().ConfigureAwait(false) is Role.Master master ? master.Replicas.Count : null;
        }
        catch (Exception ex) when (Denies(ex))
        {
            denied.Add(Denial("ROLE", server.EndPoint));
            return null;
        }
        catch (RedisTimeoutException)
        {
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
                denied.Add(Denial("WAIT", server.EndPoint));
                return false;
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                // A timeout counts as one more write without an acknowledgement.
            }
        }

        return false;
    }

    /// <summary>A two-key script across two slots, sent to one server: CROSSSLOT means the server enforces slots.</summary>
    private static async Task<bool> SlotEnforcedAsync(IServer server, string probePrefix, string nonce, List<string> denied)
    {
        var first = $"{probePrefix}probe:{{{SlotTags.For(0)}}}:{nonce}";
        var second = $"{probePrefix}probe:{{{SlotTags.For(8000)}}}:{nonce}";
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
            denied.Add(Denial("EVAL", server.EndPoint));
            return true;
        }
    }

    /// <summary><c>SCAN 0 MATCH pattern COUNT n TYPE string</c> answers and its loop ends, on every connected primary, or on the one unit a proxy shows.</summary>
    /// <remarks>The walk is cancellable and bounded by <paramref name="budget"/>: a loop still running when it ends makes the scan <see cref="ScanMode.Incomplete"/>.</remarks>
    private static async Task<ScanMode> ScanAsync(IDatabase database, IServer server, string pattern, bool enforced, List<string> denied, int count, TimeSpan? budget, CancellationToken cancellationToken)
    {
        var cluster = server.ServerType == ServerType.Cluster;
        var proxy = server.ServerType is ServerType.Twemproxy or ServerType.Envoyproxy || (!cluster && enforced);
        var units = proxy ? [server] : database.Multiplexer.GetServers().Where(static s => !s.IsReplica && s.IsConnected).ToList();
        var started = Stopwatch.GetTimestamp();
        var incomplete = false;
        var current = server;
        try
        {
            foreach (var unit in units)
            {
                current = unit;
                var cursor = "0";
                while (!incomplete)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var reply = (RedisResult[])(await unit.ExecuteAsync("SCAN", cursor, "MATCH", pattern, "COUNT", count, "TYPE", "string").ConfigureAwait(false))!;
                    cursor = (string)reply[0]!;
                    if (cursor == "0")
                    {
                        break;
                    }

                    incomplete = budget is { } limit && Stopwatch.GetElapsedTime(started) >= limit;
                }
            }
        }
        catch (Exception ex) when (Denies(ex))
        {
            denied.Add(Denial("SCAN", current.EndPoint));
            return ScanMode.None;
        }
        catch (Exception ex) when (ex is RedisServerException or RedisTimeoutException)
        {
            return ScanMode.None;
        }

        return incomplete ? ScanMode.Incomplete : proxy ? ScanMode.ProxySingleUnit : ScanMode.PerShard;
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
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException or RedisCommandException)
        {
            // Blocked, or admin-only on a connection without allowAdmin. INFO below.
        }

        try
        {
            var info = (string?)await server.ExecuteAsync("INFO", "memory").ConfigureAwait(false);
            var line = info?.Split('\n').FirstOrDefault(l => l.StartsWith("maxmemory_policy:", StringComparison.Ordinal));
            return line?["maxmemory_policy:".Length..].Trim();
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException or RedisCommandException)
        {
            return null;
        }
    }
}
