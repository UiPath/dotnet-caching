namespace UiPath.Caching.Redis;
internal static class RedisUtils
{
    /// <summary>
    /// Phrase a RESP server uses when it does not implement a command. Matched as a case-insensitive substring, not a prefix:
    /// Redis answers <c>ERR unknown command 'X', with args beginning with:</c>, but a proxy can rewrite the <c>ERR</c> prefix and
    /// managed services phrase it their own way.
    /// </summary>
    private const string UnknownCommandErrorMessage = "unknown command";

    private static readonly Version ExpireTimeVersion = new(7, 0);

    internal static bool SupportsExpireTime(Version version) => version >= ExpireTimeVersion;

    internal static bool IsDenied(Exception ex) =>
        ex is RedisServerException && ex.Message.StartsWith("NOPERM", StringComparison.OrdinalIgnoreCase);

    /// <summary>An unknown command or a missing permission, as the server answers it. A client-side command error, a timeout, a server loading or a replica is not one.</summary>
    internal static bool IsServerRefusal(Exception ex) =>
        ex is RedisServerException
        && (ex.Message.Contains(UnknownCommandErrorMessage, StringComparison.OrdinalIgnoreCase)
            || ex.Message.StartsWith("NOPERM", StringComparison.OrdinalIgnoreCase));

    /// <summary>A group that already exists, matched by its BUSYGROUP code since servers reword the text after it.</summary>
    internal static bool IsGroupExists(Exception ex) =>
        ex is RedisServerException && ex.Message.StartsWith("BUSYGROUP", StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnsupportedCommand(Exception ex) =>
        ex is RedisCommandException ||
        (ex is RedisServerException &&
         ex.Message.Contains(UnknownCommandErrorMessage, StringComparison.OrdinalIgnoreCase));

    /// <summary>A script refused for good (ACL, scripting off, a command a script may not run), not one that is busy or a server that is loading.</summary>
    internal static bool RefusesScript(Exception ex) =>
        IsDenied(ex)
        || IsUnsupportedCommand(ex)
        || (ex is RedisServerException
            && (ex.Message.Contains("NOPERM", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("ACL failure", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("not allowed from script", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("can't run this command", StringComparison.OrdinalIgnoreCase)));
}
