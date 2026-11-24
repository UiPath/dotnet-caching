using StackExchange.Redis;

namespace UiPath.Caching.Migration;

/// <summary>The staging hash of a promotion or a fill: written in the destination's slot, then renamed over the destination by one script.</summary>
internal static class HashStaging
{
    private const CommandFlags Master = CommandFlags.DemandMaster;

    // Fields per command of the staging write.
    private const int Chunk = 512;

    /// <summary>Writes the fields in chunks, each with the life of the fence, and counts them before they are published: a staging key that expired between two chunks would be recreated with the later ones alone, and a partial hash is never published.</summary>
    public static async ValueTask<bool> WriteAsync(KeyMoveData data, RedisKey staging, IReadOnlyList<KeyValuePair<string, byte[]>> stored, TimeSpan life)
    {
        var entries = stored.Select(e => new HashEntry(e.Key, e.Value)).ToArray();
        foreach (var chunk in entries.Chunk(Chunk))
        {
            var transaction = data.Db.CreateTransaction();
            var written = transaction.HashSetAsync(staging, chunk, Master);
            var expiring = transaction.KeyExpireAsync(staging, life, Master);
            if (!await transaction.ExecuteAsync(Master).ConfigureAwait(false))
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
}
