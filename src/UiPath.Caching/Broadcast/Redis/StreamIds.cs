using System.Globalization;

namespace UiPath.Caching.Broadcast.Redis;

internal static class StreamIds
{
    /// <summary>
    /// The entries past the group's last delivery that were removed, if any. Redis 7.0 records the newest entry deleted
    /// and counts the entries added and read; below it, an oldest entry past the last delivery is taken as a loss.
    /// <paramref name="known"/> keeps the last read count found, for the checks where Redis cannot tell it.
    /// </summary>
    public static StreamGap? Gap(StreamInfo stream, StreamGroupInfo group, ref ReadCount? known) =>
        Gap(
            stream.Length,
            stream.Length == 0 ? stream.LastGeneratedId : stream.FirstEntry.Id,
            stream.LastGeneratedId,
            stream.MaxDeletedEntryId,
            stream.EntriesAdded,
            group.LastDeliveredId,
            group.EntriesRead is > 0 ? group.EntriesRead : null, // StackExchange.Redis reads a count Redis cannot tell as 0.
            ref known);

    /// <summary>The same, from the raw <c>XINFO STREAM</c> and <c>XINFO GROUPS</c> replies a script returned.</summary>
    public static StreamGap? Gap(RedisResult stream, RedisResult groups, RedisValue groupName, ref ReadCount? known)
    {
        var info = stream.ToDictionary();
        var length = (long)info["length"];
        var lastGenerated = (RedisValue)info["last-generated-id"];
        var oldest = length == 0 ? lastGenerated : (RedisValue)Items(info["first-entry"])[0];
        var maxDeleted = info.TryGetValue("max-deleted-entry-id", out var deleted) ? (RedisValue)deleted : RedisValue.Null;
        var added = info.TryGetValue("entries-added", out var entriesAdded) ? (long)entriesAdded : 0;
        foreach (var group in Items(groups))
        {
            var fields = group.ToDictionary();
            if ((RedisValue)fields["name"] != groupName)
            {
                continue;
            }

            long? read = fields.TryGetValue("entries-read", out var entriesRead) && !entriesRead.IsNull ? (long)entriesRead : null;
            return Gap(length, oldest, lastGenerated, maxDeleted, added, (RedisValue)fields["last-delivered-id"], read, ref known);
        }

        return null;
    }

    /// <summary>The entries of an <c>XREADGROUP</c> reply over one stream, none when it returned nil.</summary>
    public static StreamEntry[] Entries(RedisResult reply)
    {
        if (reply.IsNull)
        {
            return [];
        }

        var entries = Items(Items(Items(reply)[0])[1]);
        var parsed = new StreamEntry[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = Items(entries[i]);
            var pairs = Items(entry[1]);
            var values = new NameValueEntry[pairs.Length / 2];
            for (var j = 0; j < values.Length; j++)
            {
                values[j] = new NameValueEntry((RedisValue)pairs[2 * j], (RedisValue)pairs[(2 * j) + 1]);
            }

            parsed[i] = new StreamEntry((RedisValue)entry[0], values);
        }

        return parsed;
    }

    /// <summary>The elements of an array reply, none for nil.</summary>
    public static RedisResult[] Items(RedisResult reply) => (RedisResult[]?)reply ?? [];

    private static StreamGap? Gap(
        long length,
        RedisValue oldest,
        RedisValue lastGenerated,
        RedisValue maxDeleted,
        long entriesAdded,
        RedisValue lastDelivered,
        long? entriesRead,
        ref ReadCount? known)
    {
        if (!TryParse(lastDelivered, out var delivered))
        {
            return null;
        }

        // Redis reports no count for a group created at $ until it reads, nor after it reads past a deleted entry. The count
        // is then every entry added while the group is at the newest entry, or the one found while it was at the same entry.
        if (entriesRead is null && lastDelivered == lastGenerated)
        {
            entriesRead = entriesAdded;
        }
        else if (entriesRead is null && known is { } last && last.LastDelivered == lastDelivered)
        {
            entriesRead = last.EntriesRead;
        }

        known = entriesRead is { } count ? new ReadCount(lastDelivered, count) : null;

        var pastOldest = TryParse(oldest, out var first) && first.CompareTo(delivered) > 0;
        if (!TryParse(maxDeleted, out var deleted))
        {
            return pastOldest ? new StreamGap(lastDelivered, oldest, 0) : null;
        }

        // XDEL records the entry it removed; a trim shows only in the counts, so with none known one is assumed.
        var removed = entriesAdded - length;
        var trimmed = pastOldest && (entriesRead is not { } read || entriesAdded - read > length);
        return trimmed || deleted.CompareTo(delivered) > 0 ? new StreamGap(lastDelivered, maxDeleted, removed) : null;
    }

    private static bool TryParse(RedisValue id, out (ulong Ms, ulong Seq) parsed)
    {
        parsed = default;
        var text = (string?)id;
        var dash = text?.IndexOf('-', StringComparison.Ordinal) ?? -1;
        return dash > 0
            && ulong.TryParse(text.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out parsed.Ms)
            && ulong.TryParse(text.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out parsed.Seq);
    }
}
