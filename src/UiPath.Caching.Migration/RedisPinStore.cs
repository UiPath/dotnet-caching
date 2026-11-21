using StackExchange.Redis;

namespace UiPath.Caching.Migration;

/// <summary>The switch state and acknowledgement records in Redis, one cluster slot per application and group, changed by atomic scripts.</summary>
internal sealed class RedisPinStore(RouterAdvertisementStore nodes, string keyPrefix, string? scope) : IPinStore
{
    private const string Switched = "1";
    private const long AckExpiryMilliseconds = 24L * 60 * 60 * 1000;

    private const string PublishScript = """
        local current = redis.call('HGET', KEYS[1], ARGV[1])
        local write = true
        if current then
            local incarnation, generation = string.match(current, '^(.*):(%d+)$')
            if incarnation == ARGV[2] and tonumber(generation) > tonumber(ARGV[3]) then
                write = false
            end
        end
        if write then
            redis.call('HSET', KEYS[1], ARGV[1], ARGV[2] .. ':' .. ARGV[3])
            redis.call('PEXPIRE', KEYS[1], ARGV[4])
        end
        return { write and 1 or 0, redis.call('GET', KEYS[2]) }
        """;

    private const string CommitScript = """
        if #ARGV == 0 then
            return 0
        end
        for i = 1, #ARGV, 2 do
            if redis.call('HGET', KEYS[1], ARGV[i]) ~= ARGV[i + 1] then
                return 0
            end
        end
        redis.call('SET', KEYS[2], '1')
        return 1
        """;

    public RedisKey StateKey(string group) => $"{keyPrefix}mg:{{{Encode(scope ?? "_")}:{Encode(group)}}}:sw";

    public RedisKey AckKey(string group) => $"{keyPrefix}mg:{{{Encode(scope ?? "_")}:{Encode(group)}}}:ack";

    public async Task<SwitchRead> ReadStateAsync(string group, CancellationToken cancellationToken)
    {
        var db = await nodes.DatabaseAsync(cancellationToken).ConfigureAwait(false);
        var value = await db.StringGetAsync(StateKey(group), CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
        return Parse(value);
    }

    public async Task RestoreSwitchAsync(string group, CancellationToken cancellationToken)
    {
        var db = await nodes.DatabaseAsync(cancellationToken).ConfigureAwait(false);
        await db.StringSetAsync(StateKey(group), Switched, flags: CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<NodeEntry>> ReadNodesAsync(CancellationToken cancellationToken) => nodes.ReadAllAsync(cancellationToken);

    public async Task<AckPublish> PublishAckAsync(string group, string field, string incarnation, long generation, CancellationToken cancellationToken)
    {
        var db = await nodes.DatabaseAsync(cancellationToken).ConfigureAwait(false);
        RedisValue[] arguments = [field, incarnation, generation, AckExpiryMilliseconds];
        var result = (RedisResult[]?)await db.ScriptEvaluateAsync(PublishScript, [AckKey(group), StateKey(group)], arguments, CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result is not { Length: 2 })
        {
            throw new InvalidOperationException("The acknowledgement script answered something this version does not know.");
        }

        return new AckPublish((long)result[0] == 1, Parse((RedisValue)result[1]));
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadAcksAsync(string group, CancellationToken cancellationToken)
    {
        var db = await nodes.DatabaseAsync(cancellationToken).ConfigureAwait(false);
        var entries = await db.HashGetAllAsync(AckKey(group), CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
        var records = new Dictionary<string, string>(entries.Length, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            records[entry.Name!] = entry.Value!;
        }

        return records;
    }

    public async Task<bool> CommitSwitchAsync(string group, IReadOnlyList<KeyValuePair<string, string>> acknowledgements, CancellationToken cancellationToken)
    {
        var db = await nodes.DatabaseAsync(cancellationToken).ConfigureAwait(false);
        var arguments = new RedisValue[acknowledgements.Count * 2];
        for (var i = 0; i < acknowledgements.Count; i++)
        {
            arguments[i * 2] = acknowledgements[i].Key;
            arguments[(i * 2) + 1] = acknowledgements[i].Value;
        }

        var result = await db.ScriptEvaluateAsync(CommitScript, [AckKey(group), StateKey(group)], arguments, CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
        return (long)result == 1;
    }

    private static SwitchRead Parse(RedisValue value)
    {
        if (value.IsNull)
        {
            return SwitchRead.Unswitched;
        }

        return (string?)value == Switched ? SwitchRead.Switched : SwitchRead.Unreadable;
    }

    private static string Encode(string text) =>
        text.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("{", "%7B", StringComparison.Ordinal)
            .Replace("}", "%7D", StringComparison.Ordinal);
}
