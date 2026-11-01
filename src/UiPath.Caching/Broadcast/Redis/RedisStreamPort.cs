namespace UiPath.Caching.Broadcast.Redis;

/// <summary>The stream on Redis, through the connector's current database.</summary>
internal sealed class RedisStreamPort(RedisStreamContext context, IRedisConnector redis) : IStreamPort
{
    // Runs on every read, so it returns only what the check needs: XINFO STREAM carries the first and last entries whole.
    // XINFO GROUPS grows with the number of groups, so a wake (ARGV[4] = 0) checks against the stream alone.
    private const string CheckAndReadScript = """
        local info = redis.call('XINFO', 'STREAM', KEYS[1])
        local stream = {}
        for i = 1, #info, 2 do
            local field = info[i]
            if field == 'length' or field == 'last-generated-id' or field == 'max-deleted-entry-id' or field == 'entries-added' then
                stream[#stream + 1] = field
                stream[#stream + 1] = info[i + 1]
            elseif field == 'first-entry' and info[i + 1] then
                stream[#stream + 1] = field
                stream[#stream + 1] = { info[i + 1][1] }
            end
        end
        local groups = {}
        if ARGV[4] == '1' then
            for _, group in ipairs(redis.call('XINFO', 'GROUPS', KEYS[1])) do
                for i = 1, #group, 2 do
                    if group[i] == 'name' and group[i + 1] == ARGV[1] then
                        groups[1] = group
                    end
                end
            end
        end
        local entries
        if ARGV[5] == '1' then
            entries = redis.call('XREADGROUP', 'GROUP', ARGV[1], ARGV[2], 'COUNT', ARGV[3], 'NOACK', 'STREAMS', KEYS[1], '>')
        else
            entries = redis.call('XREADGROUP', 'GROUP', ARGV[1], ARGV[2], 'COUNT', ARGV[3], 'STREAMS', KEYS[1], '>')
        end
        return { stream, groups, entries }
        """;

    public Task<StreamEntry[]> ReadGroupAsync(RedisValue position, bool noAck) => noAck
        ? redis.Database.StreamReadGroupAsync(context.Topic, context.ConsumerGroup, context.ConsumerName, position, context.PollBatchSize, noAck: true)
        : redis.Database.StreamReadGroupAsync(context.Topic, context.ConsumerGroup, context.ConsumerName, position, context.PollBatchSize);

    public Task<RedisResult> CheckAndReadAsync(bool full) =>
        redis.Database.ScriptEvaluateAsync(
            CheckAndReadScript,
            [context.Topic],
            [context.ConsumerGroup, context.ConsumerName, context.PollBatchSize, full ? 1 : 0, context.AutoAck ? 1 : 0]);

    public CheckedRead CheckThenRead(bool full, bool noAck)
    {
        // Issued without a wait between them, to the primary like the XREADGROUP (a write), so the one connection to it
        // carries them in order and the server runs them back to back.
        var database = redis.Database;
        var stream = database.StreamInfoAsync(context.Topic, CommandFlags.DemandMaster);
        var groups = full ? database.StreamGroupInfoAsync(context.Topic, CommandFlags.DemandMaster) : null;
        var entries = ReadGroupAsync(StreamConstants.UndeliveredMessages, noAck);
        return new(StateAsync(stream), groups is null ? Task.FromResult<GroupPosition?>(null) : PositionAsync(groups), entries);
    }

    public Task<StreamEntry[]> RangeAsync(RedisValue minId, RedisValue maxId, int count) => redis.Database.StreamRangeAsync(context.Topic, minId, maxId, count);

    public Task<StreamHead?> HeadAsync() => ConsumerGroups.HeadAsync(redis.Database, context.Topic);

    public Task<bool> CreateGroupAsync(RedisValue position, StreamHead? head) => ConsumerGroups.CreateAsync(redis.Database, context.Topic, context.ConsumerGroup, position, head);

    public Task<long> AcknowledgeAsync(RedisValue[] ids) => redis.Database.StreamAcknowledgeAsync(context.Topic, context.ConsumerGroup, ids);

    private static async Task<StreamState> StateAsync(Task<StreamInfo> stream) => StreamIds.Parse(await stream.ConfigureAwait(false));

    private async Task<GroupPosition?> PositionAsync(Task<StreamGroupInfo[]> groups) => StreamIds.Group(await groups.ConfigureAwait(false), context.ConsumerGroup);
}
