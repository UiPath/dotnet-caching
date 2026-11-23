using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    // KEYS 1 target, 2 guard record (the target itself for a record or the state), 3 lease (optional). ARGV 1 mode, 2 identity or epoch, 3 lease token.
    private const string GuardedDeleteScript = """
        if #KEYS > 2 and redis.call('GET', KEYS[3]) ~= ARGV[3] then return -7 end
        local held = redis.call('HGET', KEYS[2], ARGV[1] == 'ep' and 'ep' or 'id')
        if ARGV[1] == 'id' then
          if held ~= ARGV[2] then return -1 end
        elseif ARGV[1] == 'idopt' then
          if held and held ~= ARGV[2] then return -1 end
        else
          if held and tonumber(held) > tonumber(ARGV[2]) then return -1 end
        end
        return redis.call('DEL', KEYS[1])
        """;

    /// <summary>PURGE: deletes <paramref name="target"/> only while <paramref name="guard"/> passes its check, and, when a lease is given, only while it is the caller's.</summary>
    /// <param name="guard">The record the check reads: the control record of the target's slot, or the target itself.</param>
    /// <param name="value">The identity to match, or the epoch for <see cref="DeleteGuard.EpochAtMost"/>.</param>
    /// <param name="fence">A lease in the guard's slot; a data key's slot holds none, so a data purge passes null and the caller checks the lease around each batch.</param>
    /// <returns>-1 when the guard refuses, <see cref="LeaseNotHeld"/> when the lease is not the caller's, otherwise the delete count.</returns>
    public async Task<long> GuardedDeleteAsync(RedisKey target, RedisKey guard, DeleteGuard mode, string value, LeaseFence? fence = null)
    {
        var token = fence?.Token ?? string.Empty;
        var reply = await EvaluateAsync(GuardedDeleteScript, fence is null ? [target, guard] : [target, guard, fence.Key], [ModeName(mode), value, token]).ConfigureAwait(false);
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

            var field = mode == DeleteGuard.EpochAtMost ? StateFields.Ep : StateFields.Id;
            var held = await _db.HashGetAsync(guard, field, Master).ConfigureAwait(false);
            if (!Passes(mode, held, value))
            {
                return IdentityMismatch;
            }

            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            AddFence(transaction, fence);
            transaction.AddCondition(held.IsNull ? Condition.HashNotExists(guard, field) : Condition.HashEqual(guard, field, held));
            var deleted = transaction.KeyDeleteAsync(target, Master);
            if (!await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
            {
                return null;
            }

            return await deleted.ConfigureAwait(false) ? 1 : 0;
        }).ConfigureAwait(false);
    }

    private static string ModeName(DeleteGuard mode) => mode switch
    {
        DeleteGuard.Identity => "id",
        DeleteGuard.IdentityOrNoRecord => "idopt",
        _ => "ep",
    };

    private static bool Passes(DeleteGuard mode, RedisValue held, string value) => mode switch
    {
        DeleteGuard.Identity => held == value,
        DeleteGuard.IdentityOrNoRecord => held.IsNull || held == value,
        _ => held.IsNull || (long.TryParse((string?)held, out var ep) && long.TryParse(value, out var limit) && ep <= limit),
    };
}
