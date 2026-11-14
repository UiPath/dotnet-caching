namespace UiPath.Caching.Negotiation;

internal sealed class RedisStreamGroupSource(IRedisConnector redis) : IStreamGroupSource
{
    public async Task<IReadOnlyList<string>> GroupsAsync(RedisKey stream, CancellationToken cancellationToken)
    {
        await redis.ConnectAsync(cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var groups = await redis.Database.StreamGroupInfoAsync(stream, CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
            return [.. groups.Select(g => g.Name)];
        }
        catch (RedisServerException ex) when (ex.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
    }

    public async Task<TimeSpan?> IdleAsync(RedisKey stream, string group, CancellationToken cancellationToken)
    {
        await redis.ConnectAsync(cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var consumers = await redis.Database.StreamConsumerInfoAsync(stream, group, CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
            return consumers.Length == 0 ? null : TimeSpan.FromMilliseconds(consumers.Min(c => c.IdleTimeInMilliseconds));
        }
        catch (RedisServerException ex) when (ex.Message.Contains("NOGROUP", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
        {
            // The maintainer deleted the group between listing and reading it: it has no consumer.
            return null;
        }
    }
}
