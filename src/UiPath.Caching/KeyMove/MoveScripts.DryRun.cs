using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    private const string DryRunStoreScript = """
        if redis.call('HGET', KEYS[1], 'id') ~= ARGV[1] then return 0 end
        if ARGV[2] == '1' then redis.call('HSET', KEYS[1], ARGV[3], ARGV[4]) end
        redis.call('DEL', KEYS[2])
        for i = 6, #ARGV, 2 do redis.call('HSET', KEYS[2], ARGV[i], ARGV[i + 1]) end
        redis.call('PEXPIRE', KEYS[2], ARGV[5])
        return 1
        """;

    /// <summary>Stores a dry run report in place of the last one and, when there is a stamp, its stamp, while the state has the expected id.</summary>
    /// <returns>1 written; 0 when the stored id differs or is absent.</returns>
    public async Task<long> DryRunStoreAsync(RedisKey state, RedisKey report, string id, string? stamp, IReadOnlyList<KeyValuePair<string, string>> fields, long ttlMilliseconds)
    {
        RedisValue[] args = [id, stamp is null ? "0" : "1", StateFields.DryRun, stamp ?? string.Empty, ttlMilliseconds, .. fields.SelectMany(f => new RedisValue[] { f.Key, f.Value })];
        var reply = await EvaluateAsync(DryRunStoreScript, [state, report], args).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            if (await _db.HashGetAsync(state, StateFields.Id, Master).ConfigureAwait(false) != id)
            {
                return ExpectationFailed;
            }

            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.HashEqual(state, StateFields.Id, id));
            if (stamp is not null)
            {
                queued.Add(transaction.HashSetAsync(state, StateFields.DryRun, stamp, When.Always, Master));
            }

            queued.Add(transaction.KeyDeleteAsync(report, Master));
            queued.Add(transaction.HashSetAsync(report, [.. fields.Select(f => new HashEntry(f.Key, f.Value))], Master));
            queued.Add(transaction.KeyExpireAsync(report, TimeSpan.FromMilliseconds(ttlMilliseconds), Master));
            return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? Written : null;
        }).ConfigureAwait(false);
    }
}
