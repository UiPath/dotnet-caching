namespace UiPath.Caching.Broadcast.Redis;

internal static class StreamConstants
{
    internal static readonly RedisValue UndeliveredMessages = ">";
    internal const string ConsumerGroupNameExistsErrorMessage = "BUSYGROUP Consumer Group name already exists";

    /// <summary>
    /// Phrase a RESP server uses when it does not implement the command at all. Redis-compatible stores that
    /// lack <c>XREADGROUP</c> (Garnet, for example) answer every read with it, so retrying at the poll interval
    /// is pointless: the fetch loop drops to <see cref="MaxErrorBackoff"/> instead, which still recovers if the
    /// connection later reaches a server that supports the command.
    /// <para>
    /// Matched as a case-insensitive substring rather than a prefix. Redis itself answers
    /// <c>ERR unknown command 'X', with args beginning with:</c>, but the wording is a convention rather than a
    /// contract: the <c>ERR</c> prefix can be rewritten by a proxy and managed services that disable a command
    /// phrase it their own way. A server whose wording does not match here is not mishandled, it just falls to
    /// the generic exponential backoff, which caps at the same <see cref="MaxErrorBackoff"/>; only the one-time
    /// diagnostic naming the remedy is lost.
    /// </para>
    /// </summary>
    internal const string UnknownCommandErrorMessage = "unknown command";

    /// <summary>Upper bound for the exponential backoff applied after consecutive fetch failures.</summary>
    internal static readonly TimeSpan MaxErrorBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Consecutive failures tolerated at the poll interval before backoff starts growing.</summary>
    internal const int ErrorBackoffThreshold = 3;
}
