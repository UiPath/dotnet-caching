namespace UiPath.Caching.Redis;

public sealed partial class RedisConnector
{
    /// <summary>The background SELECT check, started once after the first connection; null before it starts.</summary>
    internal Task? SelectProbe { get; private set; }

    private static async Task DeleteProbeKeyAsync(IConnectionMultiplexer multiplexer, int database, RedisKey key)
    {
        try
        {
            await multiplexer.GetDatabase(database).KeyDeleteAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
            await multiplexer.GetDatabase(0).KeyDeleteAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The key expires on its own.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The server ignores SELECT: database {Database} shares its keys with database 0, so caches in different databases see and overwrite each other's entries.")]
    private static partial void LogSelectIgnored(ILogger logger, int database);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The check that the server honours SELECT did not finish.")]
    private static partial void LogSelectProbeFailed(ILogger logger, Exception exception);

    // A server that answers +OK to SELECT but ignores it puts every database in one keyspace. Runs once per connector,
    // in the background, and only reports: any failure (a refused SELECT, NOPERM, a timeout, disposal) ends it quietly.
    // The key is raw, outside any cache key prefix, since the connector does not own one.
    private async Task ProbeSelectAsync(IConnectionMultiplexer multiplexer)
    {
        var key = (RedisKey)(SelectProbeKeyPrefix + Guid.NewGuid().ToString("N"));
        var database = -1;
        var written = false;
        try
        {
            database = multiplexer.GetDatabase().Database;
            if (database <= 0 || multiplexer.GetEndPoints().Any(endpoint => multiplexer.GetServer(endpoint).ServerType == ServerType.Cluster))
            {
                return;
            }

            var selected = multiplexer.GetDatabase(database);
            written = true;
            await selected.StringSetAsync(key, "1", expiry: TimeSpan.FromSeconds(30), when: When.Always, flags: CommandFlags.DemandMaster).ConfigureAwait(false);
            var shared = await multiplexer.GetDatabase(0).KeyExistsAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
            if (shared && !_disposed)
            {
                LogSelectIgnored(_logger, database);
            }
        }
        catch (Exception ex)
        {
            LogSelectProbeFailed(_logger, ex);
        }
        finally
        {
            if (written)
            {
                await DeleteProbeKeyAsync(multiplexer, database, key).ConfigureAwait(false);
            }
        }
    }
}
