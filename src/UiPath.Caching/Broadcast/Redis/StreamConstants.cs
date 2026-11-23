namespace UiPath.Caching.Broadcast.Redis;

internal static class StreamConstants
{
    /// <summary>Consecutive failures tolerated at the poll interval before backoff starts growing.</summary>
    internal const int ErrorBackoffThreshold = 3;

    /// <summary>One fetch timeout in this many is logged as a Warning, the rest at Debug.</summary>
    internal const int TimeoutsPerWarning = 100;

    /// <summary>One undecodable entry in this many is logged as a Warning, the rest at Debug.</summary>
    internal const int UndecodableEntriesPerWarning = 100;
    internal static readonly RedisValue UndeliveredMessages = ">";

    /// <summary>
    /// The entry field beside the payload that names the writer's source, so a reader drops its own entries without
    /// decoding them. A 3.0 reader finds the payload by its field name and ignores this one; an entry without it is decoded.
    /// </summary>
    internal static readonly RedisValue SourceField = "src";

    /// <summary>Read position of the entries delivered to a consumer but not acknowledged.</summary>
    internal static readonly RedisValue PendingMessages = "0";

    /// <summary>The position before a stream's first entry: a group made there is delivered everything the stream holds.</summary>
    internal static readonly RedisValue StreamStart = "0-0";

    /// <summary>Upper bound for the exponential backoff applied after consecutive fetch failures.</summary>
    internal static readonly TimeSpan MaxErrorBackoff = TimeSpan.FromSeconds(30);
}
