using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.Migration;

/// <summary>Router entries in the node hash, in its <c>{server ms}|{text}</c> layout, stamped and aged by the server's clock.</summary>
/// <param name="database">Acquires the database; it runs off the caller's thread and a wait on it ends with the caller's token, so a stalled connection never holds a stop.</param>
internal sealed class RouterAdvertisementStore(Func<CancellationToken, ValueTask<IDatabase>> database, RedisKey key)
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
    private Task<IDatabase>? _acquiring;

    public RedisKey Key { get; } = key;

    public async Task WriteAsync(string field, RouterAdvertisement advertisement, CancellationToken cancellationToken)
    {
        var db = await AcquireAsync(cancellationToken).ConfigureAwait(false);
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
        var db = await AcquireAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Every field of the hash, library advertisements included, with its server-stamped age; a field with no readable stamp is left out.</summary>
    public async Task<IReadOnlyList<NodeEntry>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var db = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var now = await ServerMillisecondsAsync(db, cancellationToken).ConfigureAwait(false);
        var entries = await db.HashGetAllAsync(Key, CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<NodeEntry>(entries.Length);
        foreach (var entry in entries)
        {
            var text = (string?)entry.Value;
            var separator = text?.IndexOf(StampSeparator, StringComparison.Ordinal) ?? -1;
            if (separator > 0 && long.TryParse(text![..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var stamp))
            {
                result.Add(new NodeEntry(((string?)entry.Name)!, text[(separator + 1)..], TimeSpan.FromMilliseconds(Math.Max(0, now - stamp))));
            }
        }

        return result;
    }

    /// <summary>Acquires the database under the caller's token, as the entry reads and writes do.</summary>
    public Task<IDatabase> DatabaseAsync(CancellationToken cancellationToken) => AcquireAsync(cancellationToken);

    /// <summary>Removes an entry only while it still holds the stamp it was read with, so a router that wrote again in between keeps its entry; nothing without scripts.</summary>
    public async Task PruneAsync(IReadOnlyCollection<(string Field, long Stamp)> stale, CancellationToken cancellationToken)
    {
        if (stale.Count == 0)
        {
            return;
        }

        if (!_scriptsRefused)
        {
            var db = await AcquireAsync(cancellationToken).ConfigureAwait(false);
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

        // Without a script the compare and the delete are two commands, and a router that wrote in between would lose its entry; readers age it out instead.
    }

    private static bool IsScriptRefusal(Exception ex) =>
        ex is RedisCommandException
        || (ex is RedisServerException server
            && (server.Message.StartsWith("NOPERM", StringComparison.OrdinalIgnoreCase)
                || server.Message.StartsWith("NOSCRIPT", StringComparison.OrdinalIgnoreCase)
                || server.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
                || server.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase)));

    /// <summary>One acquisition at a time: a caller that stops waiting leaves it running, retained and with its fault observed, for the next caller to reuse.</summary>
    private async Task<IDatabase> AcquireAsync(CancellationToken cancellationToken)
    {
        var pending = Volatile.Read(ref _acquiring);
        if (pending is null || pending.IsCompleted)
        {
            var started = Task.Run(() => database(cancellationToken).AsTask(), CancellationToken.None);
            _ = started.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            pending = Interlocked.CompareExchange(ref _acquiring, started, pending) == pending ? started : Volatile.Read(ref _acquiring)!;
        }

        var acquired = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return acquired;
    }

    private async Task<long> ServerMillisecondsAsync(IDatabase db, CancellationToken cancellationToken)
    {
        var time = (RedisResult[]?)await db.ExecuteAsync("TIME", [], CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
        return (long.Parse((string)time![0]!, CultureInfo.InvariantCulture) * 1000) + (long.Parse((string)time[1]!, CultureInfo.InvariantCulture) / 1000);
    }
}
