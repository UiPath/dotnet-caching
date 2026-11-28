using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    // KEYS 1 state, 2 fence (optional). ARGV 1 expected id (empty for none), 2 fence token, 3 number of fields to set, then name and value pairs, then the names to delete.
    private const string FieldsScript = """
        if #KEYS > 1 and redis.call('GET', KEYS[2]) ~= ARGV[2] then return -7 end
        if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
        if ARGV[1] ~= '' and redis.call('HGET', KEYS[1], 'id') ~= ARGV[1] then return -1 end
        local n = tonumber(ARGV[3])
        for i = 1, n do redis.call('HSET', KEYS[1], ARGV[2 + 2 * i], ARGV[3 + 2 * i]) end
        for i = 4 + 2 * n, #ARGV do redis.call('HDEL', KEYS[1], ARGV[i]) end
        return 1
        """;

    private const string GuardScript = $$"""
        if redis.call('HGET', KEYS[1], 'id') ~= ARGV[1] then return 0 end
        if ARGV[2] ~= '' and redis.call('GET', KEYS[2]) ~= ARGV[2] then return -7 end
        if ARGV[3] == '1' and (redis.call('HGET', KEYS[1], 'st') ~= '{{KeyMoveStates.NotStarted}}' or redis.call('HEXISTS', KEYS[1], 'moveId') == 1) then return -8 end
        return 1
        """;

    /// <summary>Whether the state still holds <paramref name="id"/> and the fence's lease is still the caller's; writes nothing.</summary>
    /// <param name="neverStarted">Also requires the not-started state that no X1, X7 or X13 ever stamped with <c>moveId</c>, which is the durable evidence that no control record was ever written.</param>
    /// <returns>1 when all hold; 0 when the id differs; <see cref="LeaseNotHeld"/> when the lease is not the caller's; <see cref="GroupStarted"/> when <paramref name="neverStarted"/> fails.</returns>
    public async Task<long> GuardAsync(RedisKey state, string id, LeaseFence? fence, bool neverStarted = false)
    {
        var reply = await EvaluateAsync(GuardScript, KeysOf(state, fence), [id, fence?.Token ?? string.Empty, neverStarted ? "1" : "0"]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        if (await _db.HashGetAsync(state, StateFields.Id, Master).ConfigureAwait(false) != id)
        {
            return 0;
        }

        if (fence is not null && !await HoldsFenceAsync(fence).ConfigureAwait(false))
        {
            return LeaseNotHeld;
        }

        if (neverStarted && (await _db.HashGetAsync(state, StateFields.State, Master).ConfigureAwait(false) != KeyMoveStates.NotStarted || await _db.HashExistsAsync(state, StateFields.MoveId, Master).ConfigureAwait(false)))
        {
            return GroupStarted;
        }

        return 1;
    }

    /// <summary>Sets and deletes plain fields of an existing state hash, only while its id is <paramref name="expectedId"/> when one is given; never creates the hash.</summary>
    /// <returns>1 written; 0 when there is no hash; -1 when the stored id differs; <see cref="LeaseNotHeld"/> when a fence is given and its lease is not the caller's.</returns>
    public async Task<long> FieldsAsync(RedisKey state, string expectedId, IReadOnlyList<(string Field, string Value)> set, IReadOnlyList<string> delete, LeaseFence? fence = null)
    {
        RedisValue[] arguments = [expectedId, fence?.Token ?? string.Empty, set.Count, .. set.SelectMany(s => new RedisValue[] { s.Field, s.Value }), .. delete.Select(d => (RedisValue)d)];
        var reply = await EvaluateAsync(FieldsScript, KeysOf(state, fence), arguments).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            if (fence is not null && !await HoldsFenceAsync(fence).ConfigureAwait(false))
            {
                return LeaseNotHeld;
            }

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
            AddFence(transaction, fence);
            if (expectedId.Length > 0)
            {
                transaction.AddCondition(Condition.HashEqual(state, StateFields.Id, expectedId));
            }

            QueueFieldChanges(transaction, state, set, delete, queued);
            return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? Written : null;
        }).ConfigureAwait(false);
    }

    private static void QueueFieldChanges(ITransaction transaction, RedisKey state, IReadOnlyList<(string Field, string Value)> set, IReadOnlyList<string> delete, List<Task> queued)
    {
        foreach (var (field, value) in set)
        {
            queued.Add(transaction.HashSetAsync(state, field, value, When.Always, Master));
        }

        if (delete.Count > 0)
        {
            queued.Add(transaction.HashDeleteAsync(state, [.. delete.Select(d => (RedisValue)d)], Master));
        }

    }
}
