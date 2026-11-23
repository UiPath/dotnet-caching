using System.Globalization;
using System.Net;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>Probes one side of a move: every master its view reaches, then scan, slot enforcement, and every command it denies.</summary>
/// <remarks>
/// Each master is probed for hash commands, scripts, a watched transaction, replicas, WAIT and eviction policy. The script and the transaction run every
/// command the move uses on disposable keys that carry a per-call nonce. Run it on a connection of its own: the WAIT probe blocks the connection for up
/// to 15 s on a server whose replica does not answer. Key commands run through the side's view, since the user's ACL may restrict key patterns; ROLE,
/// WAIT, CONFIG and INFO run on the master's own server. Every probe key gets its expiry in the command or script that creates it, and DEL and PEXPIRE
/// are tried on absent keys before the first write; a master whose probe keys cannot be deleted is reported in <see cref="SideCaps.Uncleaned"/>. Time
/// (the scan budget, the probe expiry) comes from the <see cref="TimeProvider"/> the caller passes.
/// </remarks>
internal static class SideProbe
{
    // SET, GET, EXISTS, PEXPIRE, PEXPIREAT, PERSIST, RENAME, TIME, HSET, HSETNX, HGETALL, HKEYS, HLEN and HDEL: every command the move uses. A key is created
    // by SET ... PX or followed at once by PEXPIRE (after PERSIST and after HSET), so a command that fails halfway leaves a key that expires.
    private const string ProbeScript =
        "local t = redis.call('TIME') "
        + "redis.call('SET', KEYS[1], 'v', 'PX', 60000) redis.call('GET', KEYS[1]) redis.call('EXISTS', KEYS[1]) "
        + "redis.call('PEXPIRE', KEYS[1], 60001) redis.call('PEXPIREAT', KEYS[1], (tonumber(t[1]) + 60) * 1000 + 1) redis.call('PERSIST', KEYS[1]) redis.call('PEXPIRE', KEYS[1], 60001) "
        + "redis.call('RENAME', KEYS[1], KEYS[2]) redis.call('PEXPIRE', KEYS[2], 60001) "
        + "redis.call('HSET', KEYS[1], 'f', 'v') redis.call('PEXPIRE', KEYS[1], 60001) redis.call('HSETNX', KEYS[1], 'g', 'v') redis.call('HGETALL', KEYS[1]) redis.call('HKEYS', KEYS[1]) "
        + "redis.call('HLEN', KEYS[1]) redis.call('HDEL', KEYS[1], 'f', 'g') return 1";

    private const string TwoKeyScript = "return redis.call('EXISTS', KEYS[1], KEYS[2])";
    private const int WaitWrites = 5;
    private const long WaitMilliseconds = 3000;
    private const long ProbeExpiryMilliseconds = 60001;
    private const int DefaultScanCount = 1000;
    private static readonly TimeSpan DefaultScanBudget = TimeSpan.FromSeconds(30);

    /// <param name="database">The side's view: the Src prefixed view or the Dst view.</param>
    /// <param name="probePrefix">The library prefix and keyspace probe keys are written under, so they never meet application keys.</param>
    /// <param name="scanPattern">The pattern the mover scans with on this side, matched as the server stores keys.</param>
    /// <param name="slotEnforced">Set by configuration instead of probed, for a server whose policy is known.</param>
    /// <param name="cancellationToken">Cancels the probe, including the scan, which can walk the whole keyspace of every primary.</param>
    /// <param name="scanCount">The COUNT of each scan step; tests lower it to force many steps.</param>
    /// <param name="timeProvider">The clock of the scan budget and the probe expiry; <see cref="TimeProvider.System"/> when null.</param>
    public static Task<SideCaps> ProbeAsync(
        IDatabase database,
        string probePrefix,
        string scanPattern,
        bool? slotEnforced = null,
        CancellationToken cancellationToken = default,
        int scanCount = DefaultScanCount,
        TimeProvider? timeProvider = null) =>
        ProbeAsync(database, probePrefix, scanPattern, DefaultScanBudget, slotEnforced, cancellationToken, scanCount, timeProvider);

    /// <param name="database">The side's view: the Src prefixed view or the Dst view.</param>
    /// <param name="probePrefix">The library prefix and keyspace probe keys are written under, so they never meet application keys.</param>
    /// <param name="scanPattern">The pattern the mover scans with on this side, matched as the server stores keys.</param>
    /// <param name="scanBudget">The time the scan may take, 30 s by default of the other overload; null is a full traversal. The scan is then <see cref="ScanMode.Incomplete"/>.</param>
    /// <param name="slotEnforced">Set by configuration instead of probed, for a server whose policy is known.</param>
    /// <param name="cancellationToken">Cancels the probe, including the scan, which can walk the whole keyspace of every primary.</param>
    /// <param name="scanCount">The COUNT of each scan step; tests lower it to force many steps.</param>
    /// <param name="timeProvider">The clock of the scan budget and the probe expiry; <see cref="TimeProvider.System"/> when null.</param>
    public static async Task<SideCaps> ProbeAsync(
        IDatabase database,
        string probePrefix,
        string scanPattern,
        TimeSpan? scanBudget,
        bool? slotEnforced = null,
        CancellationToken cancellationToken = default,
        int scanCount = DefaultScanCount,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var nonce = MoveScripts.NewNonce();
        var denied = new List<string>();
        var (targets, unreachable) = await MastersAsync(database, probePrefix, nonce, cancellationToken).ConfigureAwait(false);
        var masters = new List<MasterCaps>();
        var uncleaned = new List<EndPoint>();
        foreach (var (endpoint, key) in targets)
        {
            masters.Add(await MasterAsync(database, database.Multiplexer.GetServer(endpoint), key, denied, uncleaned, clock).ConfigureAwait(false));
        }

        var first = database.Multiplexer.GetServer(targets[0].EndPoint);
        var enforced = slotEnforced ?? (!masters.TrueForAll(static m => m.Scripts) || await SlotEnforcedAsync(first, probePrefix, nonce, denied).ConfigureAwait(false));
        var scan = await ScanAsync(database, first, scanPattern, enforced, denied, scanCount, scanBudget, clock, cancellationToken).ConfigureAwait(false);
        return new SideCaps(masters, scan, enforced, denied, unreachable) { Uncleaned = uncleaned };
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

    private static async Task<MasterCaps> MasterAsync(IDatabase database, IServer server, string key, List<string> denied, List<EndPoint> uncleaned, TimeProvider clock)
    {
        RedisKey first = $"{key}:a";
        RedisKey second = $"{key}:b";
        var (canDelete, canExpire) = await PreflightAsync(database, server.EndPoint, first, second, denied).ConfigureAwait(false);
        try
        {
            var hashes = await HashesAsync(database, server.EndPoint, first, denied, canDelete || canExpire).ConfigureAwait(false);
            await CleanAsync(database, server.EndPoint, first, second, canDelete, uncleaned).ConfigureAwait(false);
            var scripts = await ScriptsAsync(database, server.EndPoint, first, second, denied).ConfigureAwait(false);
            await CleanAsync(database, server.EndPoint, first, second, canDelete, uncleaned).ConfigureAwait(false);
            var transactions = await TransactionsAsync(database, server.EndPoint, first, second, denied, clock).ConfigureAwait(false);
            await CleanAsync(database, server.EndPoint, first, second, canDelete, uncleaned).ConfigureAwait(false);
            var replicas = await ReplicasAsync(server, denied).ConfigureAwait(false);
            var wait = replicas != 0 && await WaitAsync(database, server, first, denied).ConfigureAwait(false);
            var eviction = await EvictionPolicyAsync(server).ConfigureAwait(false);
            var (clientIdDenied, infoDenied, needsAdmin) = await ProofAsync(server, denied).ConfigureAwait(false);
            return new MasterCaps(server.EndPoint, server.Version, scripts, transactions, hashes, wait, replicas, eviction)
            {
                ClientIdDenied = clientIdDenied,
                InfoDenied = infoDenied,
                NeedsAdmin = needsAdmin,
            };
        }
        finally
        {
            await CleanAsync(database, server.EndPoint, first, second, canDelete, uncleaned).ConfigureAwait(false);
        }
    }

    /// <summary>DEL and PEXPIRE on keys that do not exist, before the first write: whether the probe keys can be deleted, and whether a key can be given an expiry.</summary>
    /// <remarks>ACL checks the command, not the key's existence. A denied DEL is reported; every probe write still carries its expiry, so a key that cannot be deleted expires.</remarks>
    private static async Task<(bool CanDelete, bool CanExpire)> PreflightAsync(IDatabase database, EndPoint endpoint, RedisKey first, RedisKey second, List<string> denied)
    {
        var canDelete = true;
        var canExpire = true;
        try
        {
            await database.KeyDeleteAsync([first, second], CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (RedisServerException ex)
        {
            canDelete = false;
            if (Denies(ex))
            {
                denied.Add(Denial("DEL", endpoint));
            }
        }

        try
        {
            await database.KeyExpireAsync(first, TimeSpan.FromMilliseconds(ProbeExpiryMilliseconds), CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (RedisServerException)
        {
            canExpire = false;
        }

        return (canDelete, canExpire);
    }

    /// <summary>Deletes the probe keys, so a script that stopped halfway on a denial does not leave a key of the wrong type for the next probe.</summary>
    /// <remarks>A master whose keys cannot be deleted is added to <paramref name="uncleaned"/> once: the keys expire on their own, and the report says so.</remarks>
    private static async Task CleanAsync(IDatabase database, EndPoint endpoint, RedisKey first, RedisKey second, bool canDelete, List<EndPoint> uncleaned)
    {
        try
        {
            if (canDelete)
            {
                await database.KeyDeleteAsync([first, second], CommandFlags.DemandMaster).ConfigureAwait(false);
                return;
            }
        }
        catch (RedisServerException)
        {
            // Reported below, never dropped.
        }

        if (!uncleaned.Contains(endpoint))
        {
            uncleaned.Add(endpoint);
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
    /// <remarks>The first HSET, which creates the hash, runs with its PEXPIRE in one script, or in one transaction where scripts are refused. It does not run at all when the key could neither be deleted nor given an expiry.</remarks>
    private static async Task<bool> HashesAsync(IDatabase database, EndPoint endpoint, RedisKey key, List<string> denied, bool safe)
    {
        try
        {
            if (!safe || !await CreateHashAsync(database, key).ConfigureAwait(false))
            {
                denied.Add(Denial("HASH", endpoint));
                return false;
            }

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

    /// <summary>HSET and PEXPIRE together: one script, else one transaction, else the two commands one after the other; false when the transaction ran but did not commit.</summary>
    /// <remarks>A denial of HSET itself throws, and <see cref="HashesAsync"/> reports it. The last form is not atomic, but is the only one on a server that refuses both scripts and transactions.</remarks>
    private static async Task<bool> CreateHashAsync(IDatabase database, RedisKey key)
    {
        try
        {
            await database.ScriptEvaluateAsync(
                $"redis.call('HSET', KEYS[1], 'f', 'v') redis.call('PEXPIRE', KEYS[1], {ProbeExpiryMilliseconds}) return 1",
                [key],
                flags: CommandFlags.DemandMaster).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (RedisUtils.RefusesScript(ex))
        {
            // Scripts are refused or the denial is inside this one: the transaction decides.
        }

        try
        {
            var transaction = database.CreateTransaction();
            var set = transaction.HashSetAsync(key, "f", "v");
            var expire = transaction.KeyExpireAsync(key, TimeSpan.FromMilliseconds(ProbeExpiryMilliseconds));
            var executed = await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
            await Task.WhenAll(set, expire).ConfigureAwait(false);
            return executed;
        }
        catch (Exception ex) when (Denies(ex) || (ex is RedisServerException && (ex.Message.Contains("EXEC without MULTI", StringComparison.OrdinalIgnoreCase) || ex.Message.StartsWith("EXECABORT", StringComparison.OrdinalIgnoreCase))))
        {
            // Transactions are refused as well.
        }

        await database.HashSetAsync(key, "f", "v", flags: CommandFlags.DemandMaster).ConfigureAwait(false);
        await database.KeyExpireAsync(key, TimeSpan.FromMilliseconds(ProbeExpiryMilliseconds), CommandFlags.DemandMaster).ConfigureAwait(false);
        return true;
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
    private static async Task<bool> TransactionsAsync(IDatabase database, EndPoint endpoint, RedisKey first, RedisKey second, List<string> denied, TimeProvider clock)
    {
        List<Task> queued = [];
        try
        {
            var transaction = database.CreateTransaction();
            transaction.AddCondition(Condition.HashLengthEqual(first, 0));
            var atSecond = clock.GetUtcNow().UtcDateTime.AddMinutes(1);
            Task[] commands =
            [
                transaction.StringSetAsync(first, "v", TimeSpan.FromMilliseconds(ProbeExpiryMilliseconds)),
                transaction.StringGetAsync(first),
                transaction.KeyExistsAsync(first),
                transaction.KeyExpireAsync(first, TimeSpan.FromMilliseconds(60001)),
                transaction.KeyExpireAsync(first, new DateTime(atSecond.Ticks - (atSecond.Ticks % TimeSpan.TicksPerSecond) + (TimeSpan.TicksPerMillisecond * 500), DateTimeKind.Utc)),
                transaction.KeyPersistAsync(first),
                transaction.KeyExpireAsync(first, TimeSpan.FromMilliseconds(ProbeExpiryMilliseconds)),
                transaction.KeyRenameAsync(first, second),
                transaction.KeyExpireAsync(second, TimeSpan.FromMilliseconds(60001)),
                transaction.ExecuteAsync("TIME"),
                transaction.HashSetAsync(first, "f", "v"),
                transaction.KeyExpireAsync(first, TimeSpan.FromMilliseconds(ProbeExpiryMilliseconds)),
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

    /// <summary><c>CLIENT ID</c> and plain <c>INFO</c> on the master's own server: what proves a WAIT ran on the connection that sent the write.</summary>
    /// <remarks>
    /// Only an explicit refusal fails the gate: a client without allowAdmin, an ACL denial or a command the server does not know. BUSY, LOADING, a client
    /// timeout or a dropped connection proves nothing either way. INFO has no section argument, because Redis 6 refuses more than one.
    /// </remarks>
    private static async Task<(bool ClientIdDenied, bool InfoDenied, bool NeedsAdmin)> ProofAsync(IServer server, List<string> denied)
    {
        var (clientIdDenied, clientIdNeedsAdmin) = await ProofCommandAsync(server, "CLIENT ID", denied, "CLIENT", "ID").ConfigureAwait(false);
        var (infoDenied, infoNeedsAdmin) = await ProofCommandAsync(server, "INFO", denied, "INFO").ConfigureAwait(false);
        return (clientIdDenied, infoDenied, clientIdNeedsAdmin || infoNeedsAdmin);
    }

    private static async Task<(bool Denied, bool NeedsAdmin)> ProofCommandAsync(IServer server, string name, List<string> denied, string command, params object[] args)
    {
        try
        {
            await server.ExecuteAsync(command, args).ConfigureAwait(false);
            return (false, false);
        }
        catch (RedisCommandException ex) when (ex.Message.Contains("admin", StringComparison.OrdinalIgnoreCase))
        {
            return (false, true);
        }
        catch (Exception ex) when (Denies(ex) || RedisUtils.IsDenied(ex) || RedisUtils.IsUnsupportedCommand(ex))
        {
            denied.Add(Denial(name, server.EndPoint));
            return (true, false);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            // Inconclusive, like every other transient error of the probe: BUSY, LOADING, a timeout or a dropped connection.
            return (false, false);
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

    /// <summary><c>SCAN 0 MATCH pattern COUNT n TYPE string</c> answers on every connected primary, or on the one unit a proxy shows, and its loop ends.</summary>
    /// <remarks>
    /// One SCAN step runs on every unit first, so a unit that denies or errors fails the scan whatever the others do. Only then does the traversal run, from
    /// each unit's cursor; it is cancellable and bounded by <paramref name="budget"/>, and a loop still running when it ends makes the scan <see cref="ScanMode.Incomplete"/>.
    /// </remarks>
    private static async Task<ScanMode> ScanAsync(IDatabase database, IServer server, string pattern, bool enforced, List<string> denied, int count, TimeSpan? budget, TimeProvider clock, CancellationToken cancellationToken)
    {
        var cluster = server.ServerType == ServerType.Cluster;
        var proxy = server.ServerType is ServerType.Twemproxy or ServerType.Envoyproxy || (!cluster && enforced);
        var units = proxy ? [server] : database.Multiplexer.GetServers().Where(static s => !s.IsReplica && s.IsConnected).ToList();
        var current = server;
        try
        {
            var cursors = new List<string>();
            foreach (var unit in units)
            {
                current = unit;
                cancellationToken.ThrowIfCancellationRequested();
                cursors.Add(await ScanStepAsync(unit, "0", pattern, count).ConfigureAwait(false));
            }

            var started = clock.GetTimestamp();
            var incomplete = false;
            for (var i = 0; i < units.Count && !incomplete; i++)
            {
                current = units[i];
                incomplete = !await CompleteScanAsync(current, cursors[i], pattern, count, budget, clock, started, cancellationToken).ConfigureAwait(false);
            }

            if (incomplete)
            {
                return ScanMode.Incomplete;
            }

            return proxy ? ScanMode.ProxySingleUnit : ScanMode.PerShard;
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
    }

    private static async Task<bool> CompleteScanAsync(IServer unit, string cursor, string pattern, int count, TimeSpan? budget, TimeProvider clock, long started, CancellationToken cancellationToken)
    {
        while (cursor != "0")
        {
            if (budget is { } limit && clock.GetElapsedTime(started) >= limit)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            cursor = await ScanStepAsync(unit, cursor, pattern, count).ConfigureAwait(false);
        }

        return true;
    }

    /// <returns>The cursor the step ended on; "0" when the unit is done.</returns>
    private static async Task<string> ScanStepAsync(IServer unit, string cursor, string pattern, int count)
    {
        var response = await unit.ExecuteAsync("SCAN", cursor, "MATCH", pattern, "COUNT", count, "TYPE", "string").ConfigureAwait(false);
        var reply = ((RedisResult[]?)response)!;
        return ((string?)reply[0])!;
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
