using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The staging hash of a promotion or a fill: written in the destination's slot, then renamed over the destination by one script.</summary>
internal static class HashStaging
{
    private const CommandFlags Master = CommandFlags.DemandMaster;

    // Fields per command of the staging write.
    private const int Chunk = 512;

    /// <summary>Writes the fields in chunks, each with the life of the fence, and counts them before they are published: a staging key that expired between two chunks would be recreated with the later ones alone, and a partial hash is never published.</summary>
    public static async ValueTask<bool> WriteAsync(KeyMoveData data, RedisKey staging, IReadOnlyList<KeyValuePair<string, byte[]>> stored, TimeSpan life, IMoveOwnership? ownership = null, CancellationToken cancellationToken = default)
    {
        var entries = stored.Select(e => new HashEntry(e.Key, e.Value)).ToArray();
        foreach (var chunk in entries.Chunk(Chunk))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ownership is not null && !await ownership.HoldsAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new OwnershipLostException();
            }

            var transaction = data.Db.CreateTransaction();
            var written = transaction.HashSetAsync(staging, chunk, Master);
            var expiring = transaction.KeyExpireAsync(staging, life, Master);
            if (!await CommitAsync(data.Db, transaction, staging, ownership is not null).ConfigureAwait(false) && (ownership is null || !written.IsFaulted))
            {
                return false;
            }

            await written.ConfigureAwait(false);
            await expiring.ConfigureAwait(false);
        }

        return await data.Db.HashLengthAsync(staging, Master).ConfigureAwait(false) == entries.Length;
    }

    public static async ValueTask DeleteAsync(KeyMoveData data, RedisKey staging)
    {
        try
        {
            _ = await data.Db.KeyDeleteAsync(staging, Master).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            // The staging key's own life ends it.
            _ = ex;
        }
    }

    private static async Task<bool> CommitAsync(IDatabase db, ITransaction transaction, RedisKey staging, bool mover)
    {
        try
        {
            return await transaction.ExecuteAsync(Master).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (mover && ex.Kind == RedisErrorKind.ExecAbort)
        {
            // EXECABORT can hide a queue-time memory refusal; SET XX on an unused key checks the same primary without storing a value.
            _ = await db.StringSetAsync(staging.Append(":check"), RedisValue.EmptyString, TimeSpan.FromMilliseconds(1), When.Exists, Master).ConfigureAwait(false);
            return false;
        }
    }
}
