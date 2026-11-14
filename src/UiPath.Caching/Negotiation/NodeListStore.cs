using System.Globalization;
using UiPath.Caching.Broadcast.Redis;

namespace UiPath.Caching.Negotiation;

/// <summary>
/// One hash per keyspace of advertisements, each stamped with the server's clock when written and aged against the server's clock when read,
/// so a node's own clock never enters.
/// </summary>
internal sealed class NodeListStore(IRedisConnector redis, RedisKey key)
{
    private const char StampSeparator = '|';

    private const string WriteScript = """
        local t = redis.call('TIME')
        local ms = t[1] * 1000 + math.floor(t[2] / 1000)
        redis.call('HSET', KEYS[1], ARGV[1], string.format('%d', ms) .. '|' .. ARGV[2])
        return ms
        """;

    private const string ReadScript = """
        local t = redis.call('TIME')
        return { t[1] * 1000 + math.floor(t[2] / 1000), redis.call('HGETALL', KEYS[1]) }
        """;

    // Removes a field only while it still holds the stamp that was read, so a node that advertised again in between keeps its ad.
    private const string PruneScript = """
        local removed = 0
        for i = 1, #ARGV, 2 do
            local value = redis.call('HGET', KEYS[1], ARGV[i])
            if value and string.sub(value, 1, #ARGV[i + 1] + 1) == ARGV[i + 1] .. '|' then
                redis.call('HDEL', KEYS[1], ARGV[i])
                removed = removed + 1
            end
        end
        return removed
        """;

    // Off for good once the server refuses scripts; the same steps then run as commands. A transient error (READONLY, LOADING, BUSY) is not a refusal.
    private volatile bool _scriptsRefused;

    public RedisKey Key { get; } = key;

    private IDatabase Database => redis.Database;

    /// <summary>The same construction as the stream maintainer's lock key, so the connector's prefix and a cluster slot follow it.</summary>
    public static RedisKey KeyFor(CacheOptions cacheOptions, RedisCacheOptions redisOptions)
    {
        var factory = redisOptions.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory();
        return factory.Create(cacheOptions, RedisKeyspaces.String)
            .GetRedisKey(KeyEscaping.EscapeBraces(string.Join(cacheOptions.Separator, "Caching", "Nodes")));
    }

    public static bool IsScriptRefusal(Exception ex) =>
        ex is RedisCommandException
        || (ex is RedisServerException server
            && (server.Message.StartsWith("NOPERM", StringComparison.OrdinalIgnoreCase)
                || server.Message.StartsWith("NOSCRIPT", StringComparison.OrdinalIgnoreCase)
                || server.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
                || server.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase)));

    public async Task WriteAsync(string nodeId, NodeAdvertisement advertisement, CancellationToken cancellationToken)
    {
        var text = advertisement.Encode();
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!_scriptsRefused)
        {
            try
            {
                await Database.ScriptEvaluateAsync(WriteScript, [Key], [nodeId, text], CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsScriptRefusal(ex))
            {
                _scriptsRefused = true;
            }
        }

        var now = await ServerTimeAsync(cancellationToken).ConfigureAwait(false);
        await Database.HashSetAsync(Key, nodeId, string.Create(CultureInfo.InvariantCulture, $"{now}{StampSeparator}{text}"), flags: CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Entries that do not parse are left out: another version's field is not this node's to judge.</summary>
    public async Task<IReadOnlyList<StoredAdvertisement>> ReadAsync(CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        long now;
        HashEntry[] entries;
        if (_scriptsRefused)
        {
            (now, entries) = await ReadWithCommandsAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                var result = (RedisResult[])(await Database.ScriptEvaluateAsync(ReadScript, [Key], flags: CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false))!;
                now = (long)result[0];
                var flat = (RedisResult[])result[1]!;
                entries = new HashEntry[flat.Length / 2];
                for (var i = 0; i < entries.Length; i++)
                {
                    entries[i] = new HashEntry((string)flat[2 * i]!, (string)flat[(2 * i) + 1]!);
                }
            }
            catch (Exception ex) when (IsScriptRefusal(ex))
            {
                _scriptsRefused = true;
                (now, entries) = await ReadWithCommandsAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var ads = new List<StoredAdvertisement>(entries.Length);
        foreach (var entry in entries)
        {
            var text = (string?)entry.Value;
            var separator = text?.IndexOf(StampSeparator, StringComparison.Ordinal) ?? -1;
            if (separator > 0
                && long.TryParse(text![..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var stamp)
                && NodeAdvertisement.TryParse(text[(separator + 1)..], out var ad))
            {
                ads.Add(new((string)entry.Name!, ad, TimeSpan.FromMilliseconds(Math.Max(0, now - stamp)), stamp));
            }
        }

        return ads;
    }

    /// <summary>Removes the advertisements that still hold the stamp they were read with.</summary>
    public async Task PruneAsync(IReadOnlyCollection<StoredAdvertisement> stale, CancellationToken cancellationToken)
    {
        if (stale.Count == 0)
        {
            return;
        }

        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!_scriptsRefused)
        {
            try
            {
                RedisValue[] arguments = [.. stale.SelectMany(a => new RedisValue[] { a.NodeId, a.Stamp.ToString(CultureInfo.InvariantCulture) })];
                await Database.ScriptEvaluateAsync(PruneScript, [Key], arguments, CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsScriptRefusal(ex))
            {
                _scriptsRefused = true;
            }
        }

        // Without scripts the check and the delete are two commands; a node that advertises between them loses its ad until the next tick.
        foreach (var ad in stale)
        {
            var held = (string?)await Database.HashGetAsync(Key, ad.NodeId, CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (held is not null && held.StartsWith(ad.Stamp.ToString(CultureInfo.InvariantCulture) + StampSeparator, StringComparison.Ordinal))
            {
                await Database.HashDeleteAsync(Key, ad.NodeId, CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // The connector's database blocks on the first connect; connecting under the token first keeps a start with Redis down inside its budget.
    private Task ConnectAsync(CancellationToken cancellationToken) =>
        redis.ConnectAsync(cancellationToken).AsTask().WaitAsync(cancellationToken);

    private async Task<long> ServerTimeAsync(CancellationToken cancellationToken)
    {
        var time = (RedisResult[])(await Database.ExecuteAsync("TIME", args: null, flags: CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false))!;
        return ((long)time[0] * 1000) + ((long)time[1] / 1000);
    }

    private async Task<(long Now, HashEntry[] Entries)> ReadWithCommandsAsync(CancellationToken cancellationToken)
    {
        var entries = Database.HashGetAllAsync(Key, CommandFlags.DemandMaster);
        var now = ServerTimeAsync(cancellationToken);
        return (await now.ConfigureAwait(false), await entries.WaitAsync(cancellationToken).ConfigureAwait(false));
    }
}
