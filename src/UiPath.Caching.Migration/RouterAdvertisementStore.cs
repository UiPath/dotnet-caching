using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.Migration;

/// <summary>Router entries in the node hash, in its <c>{server ms}|{text}</c> layout, stamped and aged by the server's clock.</summary>
internal sealed class RouterAdvertisementStore(Func<IDatabase> database, RedisKey key)
{
    private const char StampSeparator = '|';

    private const string WriteScript = """
        local t = redis.call('TIME')
        local ms = t[1] * 1000 + math.floor(t[2] / 1000)
        redis.call('HSET', KEYS[1], ARGV[1], string.format('%d', ms) .. '|' .. ARGV[2])
        return ms
        """;

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

    private volatile bool _scriptsRefused;

    public RedisKey Key { get; } = key;

    public async Task WriteAsync(string field, RouterAdvertisement advertisement, CancellationToken cancellationToken)
    {
        var db = database();
        var text = advertisement.Encode();
        if (!_scriptsRefused)
        {
            try
            {
                await db.ScriptEvaluateAsync(WriteScript, [Key], [field, text], CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsScriptRefusal(ex))
            {
                _scriptsRefused = true;
            }
        }

        var now = await ServerMillisecondsAsync(db, cancellationToken).ConfigureAwait(false);
        await db.HashSetAsync(Key, field, string.Create(CultureInfo.InvariantCulture, $"{now}{StampSeparator}{text}"), flags: CommandFlags.DemandMaster)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every router entry with its age; library advertisements and entries that do not parse are left out.</summary>
    public async Task<IReadOnlyList<(string Field, RouterAdvertisement Advertisement, TimeSpan Age, long Stamp)>> ReadAsync(CancellationToken cancellationToken)
    {
        var db = database();
        var now = await ServerMillisecondsAsync(db, cancellationToken).ConfigureAwait(false);
        var entries = await db.HashGetAllAsync(Key, CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<(string, RouterAdvertisement, TimeSpan, long)>();
        foreach (var entry in entries)
        {
            var text = (string?)entry.Value;
            var separator = text?.IndexOf(StampSeparator, StringComparison.Ordinal) ?? -1;
            if (separator > 0
                && long.TryParse(text![..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var stamp)
                && RouterAdvertisement.TryParse(text[(separator + 1)..], out var advertisement))
            {
                result.Add((((string?)entry.Name)!, advertisement, TimeSpan.FromMilliseconds(Math.Max(0, now - stamp)), stamp));
            }
        }

        return result;
    }

    /// <summary>Removes an entry only while it still holds the stamp it was read with, so a router that wrote again in between keeps its entry.</summary>
    public async Task PruneAsync(IReadOnlyCollection<(string Field, long Stamp)> stale, CancellationToken cancellationToken)
    {
        if (stale.Count == 0)
        {
            return;
        }

        var db = database();
        if (!_scriptsRefused)
        {
            try
            {
                RedisValue[] arguments = [.. stale.SelectMany(s => new RedisValue[] { s.Field, s.Stamp.ToString(CultureInfo.InvariantCulture) })];
                await db.ScriptEvaluateAsync(PruneScript, [Key], arguments, CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsScriptRefusal(ex))
            {
                _scriptsRefused = true;
            }
        }

        foreach (var (field, stamp) in stale)
        {
            var held = (string?)await db.HashGetAsync(Key, field, CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (held is not null && held.StartsWith(stamp.ToString(CultureInfo.InvariantCulture) + StampSeparator, StringComparison.Ordinal))
            {
                await db.HashDeleteAsync(Key, field, CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsScriptRefusal(Exception ex) =>
        ex is RedisCommandException
        || (ex is RedisServerException server
            && (server.Message.StartsWith("NOPERM", StringComparison.OrdinalIgnoreCase)
                || server.Message.StartsWith("NOSCRIPT", StringComparison.OrdinalIgnoreCase)
                || server.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
                || server.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase)));

    private async Task<long> ServerMillisecondsAsync(IDatabase db, CancellationToken cancellationToken)
    {
        var time = (RedisResult[]?)await db.ExecuteAsync("TIME").WaitAsync(cancellationToken).ConfigureAwait(false);
        return (long.Parse((string)time![0]!, CultureInfo.InvariantCulture) * 1000) + (long.Parse((string)time[1]!, CultureInfo.InvariantCulture) / 1000);
    }
}
