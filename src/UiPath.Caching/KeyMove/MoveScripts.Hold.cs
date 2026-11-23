using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    public const long LeaseRenewed = 1;
    public const long LeaseAcquired = 2;
    public const long LeaseHeldByAnother = 0;

    private const string HoldSetScript = """
        if #KEYS > 1 and redis.call('GET', KEYS[2]) ~= ARGV[3] then return -7 end
        if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        return 1
        """;

    private const string HoldClearScript = """
        if #KEYS > 1 and redis.call('GET', KEYS[2]) ~= ARGV[1] then return -7 end
        if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
        for i = 2, #ARGV do redis.call('HDEL', KEYS[1], ARGV[i]) end
        return 1
        """;

    private const string LeaseScript = """
        local v = redis.call('GET', KEYS[1])
        if v == ARGV[1] then redis.call('PEXPIRE', KEYS[1], ARGV[2]) return 1 end
        if not v then redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2]) return 2 end
        return 0
        """;

    private const string LeaseRenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then redis.call('PEXPIRE', KEYS[1], ARGV[2]) return 1 end
        return 0
        """;

    private const string LeaseReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    public async Task<ScriptSupport> ScriptSupportAsync()
    {
        try
        {
            return await EvaluateAsync("return 1", [], []).ConfigureAwait(false) is not null ? ScriptSupport.Scripts : ScriptSupport.Fallback;
        }
        catch (NotSupportedException)
        {
            return ScriptSupport.Unsupported;
        }
    }

    /// <summary>Sets a hold field on an existing state hash and never creates the hash.</summary>
    /// <returns>1 written (also a repeat); 0 when there is no hash; <see cref="LeaseNotHeld"/> when a fence is given and its lease is not the caller's.</returns>
    public async Task<long> HoldSetAsync(RedisKey state, string field, string reason, LeaseFence? fence = null)
    {
        var reply = await EvaluateAsync(HoldSetScript, KeysOf(state, fence), [field, reason, fence?.Token ?? string.Empty]).ConfigureAwait(false);
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
                return ExpectationFailed;
            }

            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            AddFence(transaction, fence);
            transaction.AddCondition(Condition.KeyExists(state));
            queued.Add(transaction.HashSetAsync(state, field, reason, When.Always, Master));
            return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? Written : null;
        }).ConfigureAwait(false);
    }

    /// <summary>Deletes hold fields from an existing state hash; with a fence, only for the holder of the lease.</summary>
    /// <returns>1 when the hash exists; 0 when there is none; <see cref="LeaseNotHeld"/> when the lease is not the caller's.</returns>
    public async Task<long> HoldClearAsync(RedisKey state, LeaseFence? fence, params string[] fields)
    {
        var reply = await EvaluateAsync(HoldClearScript, KeysOf(state, fence), [fence?.Token ?? string.Empty, .. fields.Select(f => (RedisValue)f)]).ConfigureAwait(false);
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
                return ExpectationFailed;
            }

            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            AddFence(transaction, fence);
            transaction.AddCondition(Condition.KeyExists(state));
            queued.Add(transaction.HashDeleteAsync(state, [.. fields.Select(f => (RedisValue)f)], Master));
            return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? Written : null;
        }).ConfigureAwait(false);
    }

    /// <returns><see cref="LeaseAcquired"/>, <see cref="LeaseRenewed"/> or <see cref="LeaseHeldByAnother"/>.</returns>
    public async Task<long> LeaseAsync(RedisKey lease, string token, long ttlMilliseconds)
    {
        var reply = await EvaluateAsync(LeaseScript, [lease], [token, ttlMilliseconds]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            var held = await _db.StringGetAsync(lease, Master).ConfigureAwait(false);
            if (!held.IsNull && held != token)
            {
                return LeaseHeldByAnother;
            }

            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(held.IsNull ? Condition.KeyNotExists(lease) : Condition.StringEqual(lease, token));
            queued.Add(transaction.StringSetAsync(lease, token, TimeSpan.FromMilliseconds(ttlMilliseconds), When.Always, Master));
            if (!await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
            {
                return null;
            }

            return held.IsNull ? LeaseAcquired : LeaseRenewed;
        }).ConfigureAwait(false);
    }

    /// <summary>Extends the lease only while <paramref name="token"/> still holds it; a lease that expired is never taken again.</summary>
    public async Task<bool> LeaseRenewAsync(RedisKey lease, string token, long ttlMilliseconds)
    {
        var reply = await EvaluateAsync(LeaseRenewScript, [lease], [token, ttlMilliseconds]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply == 1;
        }

        return await WatchedAsync(async () =>
        {
            if (await _db.StringGetAsync(lease, Master).ConfigureAwait(false) != token)
            {
                return ExpectationFailed;
            }

            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.StringEqual(lease, token));
            queued.Add(transaction.KeyExpireAsync(lease, TimeSpan.FromMilliseconds(ttlMilliseconds), Master));
            return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? Written : null;
        }).ConfigureAwait(false) == Written;
    }

    public async Task<bool> LeaseReleaseAsync(RedisKey lease, string token)
    {
        var reply = await EvaluateAsync(LeaseReleaseScript, [lease], [token]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply == 1;
        }

        return await WatchedAsync(async () =>
        {
            if (await _db.StringGetAsync(lease, Master).ConfigureAwait(false) != token)
            {
                return ExpectationFailed;
            }

            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.StringEqual(lease, token));
            queued.Add(transaction.KeyDeleteAsync(lease, Master));
            return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? Written : null;
        }).ConfigureAwait(false) == Written;
    }
}
