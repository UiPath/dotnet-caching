namespace UiPath.Caching.Broadcast.Redis;

/// <summary>
/// Creates a group with the count of entries before where it starts, which Redis 7.0 otherwise leaves unknown until the
/// group reads, so a trim before its first read shows in the counts.
/// </summary>
internal static class ConsumerGroups
{
    public static bool Create(IDatabase database, RedisKey stream, RedisValue group, RedisValue position)
    {
        if (position == StreamPosition.NewMessages && Start(() => database.StreamInfo(stream)) is { } start)
        {
            try
            {
                database.Execute("XGROUP", Arguments(stream, group, start));
                return true;
            }
            catch (RedisServerException ex) when (ex.Message != StreamConstants.ConsumerGroupNameExistsErrorMessage)
            {
                // Created without a count below.
            }
        }

        return database.StreamCreateConsumerGroup(stream, group, position);
    }

    public static async Task<bool> CreateAsync(IDatabase database, RedisKey stream, RedisValue group, RedisValue position)
    {
        if (position == StreamPosition.NewMessages && await StartAsync(database, stream).ConfigureAwait(false) is { } start)
        {
            try
            {
                await database.ExecuteAsync("XGROUP", Arguments(stream, group, start)).ConfigureAwait(false);
                return true;
            }
            catch (RedisServerException ex) when (ex.Message != StreamConstants.ConsumerGroupNameExistsErrorMessage)
            {
                // Created without a count below.
            }
        }

        return await database.StreamCreateConsumerGroupAsync(stream, group, position).ConfigureAwait(false);
    }

    private static async Task<ReadCount?> StartAsync(IDatabase database, RedisKey stream)
    {
        try
        {
            return Start(await database.StreamInfoAsync(stream).ConfigureAwait(false));
        }
        catch (RedisServerException ex)
        {
            return Missing(ex);
        }
    }

    private static ReadCount? Start(Func<StreamInfo> info)
    {
        try
        {
            return Start(info());
        }
        catch (RedisServerException ex)
        {
            return Missing(ex);
        }
    }

    // Starting at the newest entry rather than $ keeps the count exact if an entry is added before the group exists.
    // Below Redis 7.0 there is no max-deleted-entry-id, nor ENTRIESREAD.
    private static ReadCount? Start(StreamInfo info) =>
        info.MaxDeletedEntryId.IsNull ? null : new ReadCount(info.LastGeneratedId, info.EntriesAdded);

    // Nothing comes before 0-0, whoever creates the stream first; XINFO denied leaves the count unknown.
    private static ReadCount? Missing(RedisServerException ex) =>
        ex.Message.StartsWith("ERR no such key", StringComparison.OrdinalIgnoreCase) ? new ReadCount("0-0", 0) : null;

    private static object[] Arguments(RedisKey stream, RedisValue group, ReadCount start) =>
        ["CREATE", stream, group, start.LastDelivered, "MKSTREAM", "ENTRIESREAD", start.EntriesRead];
}
