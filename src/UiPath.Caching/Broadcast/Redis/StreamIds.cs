using System.Globalization;

namespace UiPath.Caching.Broadcast.Redis;

internal static class StreamIds
{
    /// <summary>
    /// The entries past the group's last delivery that were removed, if any. Redis 7.0 records the newest entry deleted
    /// and counts the entries added and read; below it, an oldest entry past the last delivery is taken as a loss.
    /// <paramref name="known"/> keeps the last read count found, for the checks where Redis cannot tell it.
    /// <paramref name="createdOnEmpty"/> is set for a group this node made on a stream that had never held an entry, and
    /// <paramref name="maxLength"/> is the stream's configured trim length.
    /// </summary>
    public static StreamGap? Gap(StreamInfo stream, StreamGroupInfo group, ref ReadCount? known, bool createdOnEmpty = false, long? maxLength = null) =>
        Gap(
            stream.Length,
            stream.Length == 0 ? stream.LastGeneratedId : stream.FirstEntry.Id,
            stream.LastGeneratedId,
            stream.MaxDeletedEntryId,
            stream.EntriesAdded,
            group.LastDeliveredId,
            group.EntriesRead is > 0 ? group.EntriesRead : null, // StackExchange.Redis reads a count Redis cannot tell as 0.
            ref known,
            createdOnEmpty,
            maxLength);

    /// <summary>The same, from the stream's state and the group's position as this reader knows it.</summary>
    public static StreamGap? Gap(StreamState stream, RedisValue lastDelivered, long? entriesRead, ref ReadCount? known, bool createdOnEmpty = false, long? maxLength = null) =>
        Gap(stream.Length, stream.Oldest, stream.LastGenerated, stream.MaxDeleted, stream.EntriesAdded, lastDelivered, entriesRead, ref known, createdOnEmpty, maxLength);

    /// <summary>The raw <c>XINFO STREAM</c> reply a script returned.</summary>
    public static StreamState Parse(RedisResult stream)
    {
        var info = stream.ToDictionary();
        var length = (long)info["length"];
        var lastGenerated = (RedisValue)info["last-generated-id"];
        return new(
            length,
            length == 0 ? lastGenerated : (RedisValue)Items(info["first-entry"])[0],
            lastGenerated,
            info.TryGetValue("max-deleted-entry-id", out var deleted) ? (RedisValue)deleted : RedisValue.Null,
            info.TryGetValue("entries-added", out var entriesAdded) ? (long)entriesAdded : 0);
    }

    /// <summary>The group's position in a raw <c>XINFO GROUPS</c> reply, or <c>null</c> when it is not there.</summary>
    public static (RedisValue LastDelivered, long? EntriesRead)? Group(RedisResult groups, RedisValue groupName)
    {
        foreach (var group in Items(groups))
        {
            var fields = group.ToDictionary();
            if ((RedisValue)fields["name"] != groupName)
            {
                continue;
            }

            long? read = fields.TryGetValue("entries-read", out var entriesRead) && !entriesRead.IsNull ? (long)entriesRead : null;
            return ((RedisValue)fields["last-delivered-id"], read);
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
        ref ReadCount? known,
        bool createdOnEmpty,
        long? maxLength)
    {
        if (!TryParse(lastDelivered, out var delivered))
        {
            return null;
        }

        entriesRead ??= KnownEntriesRead(entriesAdded, lastDelivered, lastGenerated, known);
        known = entriesRead is { } count ? new ReadCount(lastDelivered, count) : null;

        var pastOldest = TryParse(oldest, out var first) && first.CompareTo(delivered) > 0;
        if (!TryParse(maxDeleted, out var deleted))
        {
            return PreRedis7Gap(length, oldest, lastDelivered, delivered, first, createdOnEmpty, maxLength);
        }

        // XDEL records the entry it removed; a trim shows only in the counts, so with none known one is assumed.
        var removed = entriesAdded - length;
        var trimmed = pastOldest && (entriesRead is not { } read || entriesAdded - read > length);
        return trimmed || deleted.CompareTo(delivered) > 0 ? new StreamGap(lastDelivered, maxDeleted, removed) : null;
    }

    // Redis reports no count for a group created at $ until it reads, nor after it reads past a deleted entry. The count
    // is then every entry added while the group is at the newest entry, or the one found while it was at the same entry.
    private static long? KnownEntriesRead(long entriesAdded, RedisValue lastDelivered, RedisValue lastGenerated, ReadCount? known)
    {
        if (lastDelivered == lastGenerated)
        {
            return entriesAdded;
        }

        return known is { } last && last.LastDelivered == lastDelivered ? last.EntriesRead : null;
    }

    private static StreamGap? PreRedis7Gap(
        long length,
        RedisValue oldest,
        RedisValue lastDelivered,
        (ulong Ms, ulong Seq) delivered,
        (ulong Ms, ulong Seq) first,
        bool createdOnEmpty,
        long? maxLength)
    {
        // Before Redis 7 a group made at 0-0 on a stream that was empty then sees its first entry as past its position; nothing
        // was trimmed unless the stream has since grown to the length that trims it. The first entry written in a millisecond
        // has sequence 0, so a first entry with a higher one means the entries before it were removed.
        // Only before the group's first delivery. Redis 6 does not record an XDEL or MINID, so one that removed every entry
        // of a millisecond ahead of the first read is not seen; the same state is the normal one for a first entry in a later millisecond.
        if (createdOnEmpty && delivered == (0UL, 0UL) && first.Seq == 0 && (maxLength is not { } max || length < max))
        {
            return null;
        }

        // An oldest that does not parse leaves first at 0-0, which is never past the group.
        return first.CompareTo(delivered) > 0 ? new StreamGap(lastDelivered, oldest, 0) : null;
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
