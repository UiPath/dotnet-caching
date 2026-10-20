using System.Diagnostics;
using System.Globalization;

namespace UiPath.Caching.Broadcast.Redis;

/// <summary>
/// Creates a group with the count of entries before where it starts, which Redis 7.0 otherwise leaves unknown until the
/// group reads, so a trim before its first read shows in the counts.
/// </summary>
internal static class ConsumerGroups
{
    // The server's clock and the stream's head in one call, so both come from the node that owns the stream.
    private const string HeadScript = """
        local t = redis.call('TIME')
        local ok, info = pcall(redis.call, 'XINFO', 'STREAM', KEYS[1])
        if not ok then
            local err = type(info) == 'table' and info.err or tostring(info)
            if string.find(string.lower(err), 'no such key', 1, true) then return {t[1], t[2], false, false, false, false} end
            return redis.error_reply(err)
        end
        local f = {}
        for i = 1, #info, 2 do f[info[i]] = info[i + 1] end
        return {t[1], t[2], 1, f['last-generated-id'] or false, f['entries-added'] or false, f['max-deleted-entry-id'] or false}
        """;

    /// <summary>Makes the group at <paramref name="position"/>; at the head, at the one <paramref name="head"/> read when it is known.</summary>
    public static async Task<bool> CreateAsync(IDatabase database, RedisKey stream, RedisValue group, RedisValue position, StreamHead? head)
    {
        if (position == StreamPosition.NewMessages)
        {
            if ((head is { } known ? known.Start : await StartAsync(database, stream).ConfigureAwait(false)) is { } start)
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

            // At the id read rather than $, so what is added between that read and this create is delivered, not skipped.
            if (head is { } read)
            {
                position = read.Exists ? read.LastId : (RedisValue)"0";
            }
        }

        return await database.StreamCreateConsumerGroupAsync(stream, group, position).ConfigureAwait(false);
    }

    /// <summary>Whether an entry may have been added at or after <paramref name="since"/>, a <see cref="Stopwatch"/> timestamp; <c>true</c> when the head is unknown.</summary>
    public static bool AddedSince(StreamHead? head, long since)
    {
        if (head is not { } h)
        {
            return true;
        }

        if (!h.Exists || h.EntriesAdded == 0)
        {
            return false;
        }

        return h.LastMs is not { } last || last >= SinceMs(h, since);
    }

    /// <summary><paramref name="since"/>, a <see cref="Stopwatch"/> timestamp, on the server's clock.</summary>
    public static long SinceMs(StreamHead head, long since)
    {
        // Elapsed to the reply rather than the call, so the instant is placed early; 2 ms covers the truncated clocks.
        var elapsed = (long)Math.Ceiling(Stopwatch.GetElapsedTime(since, head.RepliedAt).TotalMilliseconds);
        return head.ServerMs - elapsed - 2;
    }

    /// <summary>The server's clock and the stream's head, or <c>null</c> when the server will not tell.</summary>
    public static async Task<StreamHead?> HeadAsync(IDatabase database, RedisKey stream)
    {
        try
        {
            var reply = await database.ScriptEvaluateAsync(HeadScript, [stream]).ConfigureAwait(false);
            var repliedAt = Stopwatch.GetTimestamp();
            if (reply.Resp2Type != ResultType.Array || (RedisResult[]?)reply is not { Length: 6 } parts)
            {
                return null;
            }

            var serverMs = ((long)parts[0] * 1000) + ((long)parts[1] / 1000);
            if (parts[2].IsNull)
            {
                return new StreamHead(serverMs, repliedAt, Exists: false, RedisValue.Null, LastMs: null, EntriesAdded: 0, Start: new ReadCount("0-0", 0));
            }

            var last = (RedisValue)parts[3];
            long? added = parts[4].IsNull ? null : (long)parts[4];
            // Below Redis 7.0 there is no max-deleted-entry-id, nor ENTRIESREAD.
            var start = parts[5].IsNull || added is null ? (ReadCount?)null : new ReadCount(last, added.Value);
            return new StreamHead(serverMs, repliedAt, Exists: true, last, MillisecondsOf(last), added, start);
        }
        catch (Exception)
        {
            // Scripts off or denied: the count comes from XINFO alone, and the head is unknown.
            return null;
        }
    }

    private static long? MillisecondsOf(RedisValue id)
    {
        var text = (string?)id;
        var dash = text?.IndexOf('-', StringComparison.Ordinal) ?? -1;
        return dash > 0 && long.TryParse(text.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? ms : null;
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
