using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    public const long UnitLeaseLost = 0;
    public const long UnitStepWritten = 1;

    // The lease, the cursor and the pass record of a unit share a slot; the write is refused unless the token still holds the lease, and it extends the lease.
    private const string UnitStepScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        if ARGV[3] == '' then redis.call('DEL', KEYS[2]) else redis.call('SET', KEYS[2], ARGV[3], 'PX', ARGV[5]) end
        if ARGV[4] ~= '' then redis.call('SET', KEYS[3], ARGV[4], 'PX', ARGV[6]) end
        return 1
        """;

    /// <summary>Stores a scan unit's cursor and, when its pass has finished, the pass record, only while <paramref name="token"/> holds the unit's lease, and extends that lease.</summary>
    /// <param name="cursor">The cursor to keep; empty deletes it, which is how a finished pass leaves no cursor.</param>
    /// <param name="pass">The pass record to store; empty stores none.</param>
    /// <returns><see cref="UnitStepWritten"/>, or <see cref="UnitLeaseLost"/> when another runner holds the lease or it lapsed.</returns>
    public async Task<long> UnitStepAsync(RedisKey lease, RedisKey cursorKey, RedisKey passKey, string token, long leaseMilliseconds, string cursor, string pass, long cursorMilliseconds, long passMilliseconds)
    {
        var reply = await EvaluateAsync(UnitStepScript, [lease, cursorKey, passKey], [token, leaseMilliseconds, cursor, pass, cursorMilliseconds, passMilliseconds]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            if (await _db.StringGetAsync(lease, Master).ConfigureAwait(false) != token)
            {
                return UnitLeaseLost;
            }

            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.StringEqual(lease, token));
            queued.Add(transaction.KeyExpireAsync(lease, TimeSpan.FromMilliseconds(leaseMilliseconds), Master));
            queued.Add(cursor.Length == 0
                ? transaction.KeyDeleteAsync(cursorKey, Master)
                : transaction.StringSetAsync(cursorKey, cursor, TimeSpan.FromMilliseconds(cursorMilliseconds), When.Always, Master));
            if (pass.Length > 0)
            {
                queued.Add(transaction.StringSetAsync(passKey, pass, TimeSpan.FromMilliseconds(passMilliseconds), When.Always, Master));
            }

            return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? UnitStepWritten : null;
        }).ConfigureAwait(false);
    }
}
