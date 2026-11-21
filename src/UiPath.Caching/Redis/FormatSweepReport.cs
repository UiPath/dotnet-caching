namespace UiPath.Caching.Redis;

/// <summary>What one pass of <see cref="RedisCacheProvider.SweepFormatsAsync{T}"/> did to the strings it looked at.</summary>
public sealed class FormatSweepReport
{
    /// <summary>Strings rewritten into the target format, keeping their TTL.</summary>
    public long Converted { get; init; }

    /// <summary>Strings a writer, a delete or another sweep changed between the read and the compare-and-set; left as that wrote them.</summary>
    public long Changed { get; init; }

    /// <summary>Strings that would not come out the same in the target format, or cannot be compared, and are left; a key found lossy is not looked at again for an hour.</summary>
    public long Lossy { get; init; }

    /// <summary>Strings that are empty (the cached-null marker) or equal to <c>default(T)</c>; they leave only by a write or by expiry.</summary>
    public long DefaultOrEmpty { get; init; }

    /// <summary>Strings over <see cref="FormatSweepOptions.MaxValueBytes"/>, left.</summary>
    public long Oversize { get; init; }

    /// <summary>Strings not rewritten because the node list does not allow the target format now, or the byte budget shared with reads was full. The first refusal by the node list ends the pass; one by the byte budget is counted and the pass goes on.</summary>
    public long Held { get; init; }

    /// <summary>Strings whose rewrite threw; logged, and left as they were.</summary>
    public long Failed { get; init; }

    /// <summary>Strings already in the target format.</summary>
    public long Current { get; init; }

    /// <summary>Strings in a format no registered serializer owns, which are left.</summary>
    public long Unreadable { get; init; }

    /// <summary>Strings offered within the last minute, by a read or an earlier pass, and skipped.</summary>
    public long Recent { get; init; }

    /// <summary>How many strings were looked at, whatever became of them.</summary>
    public long Scanned { get; init; }

    /// <summary>Where the next pass continues, as <see cref="FormatSweepOptions.Cursor"/>; null once <see cref="Completed"/>.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Whether every primary was scanned to its end; false when the budget ran out, the target format stopped being allowed, or a Redis error ended the pass.</summary>
    public bool Completed { get; init; }
}
