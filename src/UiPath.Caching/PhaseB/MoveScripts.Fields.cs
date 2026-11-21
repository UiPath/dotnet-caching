using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

internal sealed partial class MoveScripts
{
    // KEYS 1 state. ARGV 1 expected id (empty for none), 2 number of fields to set, then name and value pairs, then the names to delete.
    private const string FieldsScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
        if ARGV[1] ~= '' and redis.call('HGET', KEYS[1], 'id') ~= ARGV[1] then return -1 end
        local n = tonumber(ARGV[2])
        for i = 1, n do redis.call('HSET', KEYS[1], ARGV[1 + 2 * i], ARGV[2 + 2 * i]) end
        for i = 3 + 2 * n, #ARGV do redis.call('HDEL', KEYS[1], ARGV[i]) end
        return 1
        """;

    /// <summary>Sets and deletes plain fields of an existing state hash, only while its id is <paramref name="expectedId"/> when one is given; never creates the hash.</summary>
    /// <returns>1 written; 0 when there is no hash; -1 when the stored id differs.</returns>
    public async Task<long> FieldsAsync(RedisKey state, string expectedId, IReadOnlyList<(string Field, string Value)> set, IReadOnlyList<string> delete)
    {
        RedisValue[] arguments = [expectedId, set.Count, .. set.SelectMany(s => new RedisValue[] { s.Field, s.Value }), .. delete.Select(d => (RedisValue)d)];
        var reply = await EvaluateAsync(FieldsScript, [state], arguments).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            if (!await _db.KeyExistsAsync(state, Master).ConfigureAwait(false))
            {
                return 0;
            }

            if (expectedId.Length > 0 && await _db.HashGetAsync(state, StateFields.Id, Master).ConfigureAwait(false) != expectedId)
            {
                return IdentityMismatch;
            }

            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.KeyExists(state));
            if (expectedId.Length > 0)
            {
                transaction.AddCondition(Condition.HashEqual(state, StateFields.Id, expectedId));
            }

            foreach (var (field, value) in set)
            {
                queued.Add(transaction.HashSetAsync(state, field, value, When.Always, Master));
            }

            if (delete.Count > 0)
            {
                queued.Add(transaction.HashDeleteAsync(state, [.. delete.Select(d => (RedisValue)d)], Master));
            }

            return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? Written : null;
        }).ConfigureAwait(false);
    }
}
