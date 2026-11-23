using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    public const long AcquisitionOther = -9;

    // KEYS 1 state, 2 fence (optional). ARGV 1 fence token, 2 acquisition token.
    private const string AcquisitionScript = """
        if #KEYS > 1 and redis.call('GET', KEYS[2]) ~= ARGV[1] then return -7 end
        local pt = redis.call('HGET', KEYS[1], 'pt')
        if not pt then return 0 end
        if pt == ARGV[2] then return 1 end
        return -9
        """;

    // KEYS 1 the string. ARGV 1 the expected text (empty for none), 2 the new text.
    private const string SwapStringScript = """
        local current = redis.call('GET', KEYS[1])
        if ARGV[1] == '' then
          if current then return 0 end
        elseif current ~= ARGV[1] then
          return 0
        end
        redis.call('SET', KEYS[1], ARGV[2])
        return 1
        """;

    public async Task<long> AcquisitionAsync(RedisKey state, string token, LeaseFence? fence)
    {
        var reply = await EvaluateAsync(AcquisitionScript, KeysOf(state, fence), [fence?.Token ?? string.Empty, token]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        if (fence is not null && !await HoldsFenceAsync(fence).ConfigureAwait(false))
        {
            return LeaseNotHeld;
        }

        var stored = await _db.HashGetAsync(state, StateFields.PendToken, Master).ConfigureAwait(false);
        if (stored.IsNull)
        {
            return 0;
        }

        return stored == token ? 1 : AcquisitionOther;
    }

    public async Task<bool> SwapStringAsync(RedisKey key, string expected, string value)
    {
        var reply = await EvaluateAsync(SwapStringScript, [key], [expected, value]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply == 1;
        }

        return await WatchedAsync(async () =>
        {
            var current = await _db.StringGetAsync(key, Master).ConfigureAwait(false);
            if (expected.Length == 0 ? !current.IsNull : current != expected)
            {
                return 0;
            }

            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(expected.Length == 0 ? Condition.KeyNotExists(key) : Condition.StringEqual(key, expected));
            queued.Add(transaction.StringSetAsync(key, value, flags: Master));
            return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? 1 : null;
        }).ConfigureAwait(false) == 1;
    }
}
