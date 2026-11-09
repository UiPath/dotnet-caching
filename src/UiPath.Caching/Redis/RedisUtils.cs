using UiPath.Caching.Broadcast.Redis;

namespace UiPath.Caching.Redis;
internal static class RedisUtils
{
    private static readonly Version ExpireTimeVersion = new(7, 0);

    internal static bool SupportsExpireTime(Version version) => version >= ExpireTimeVersion;

    internal static bool IsDenied(Exception ex) =>
        ex is RedisServerException && ex.Message.StartsWith("NOPERM", StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnsupportedCommand(Exception ex) =>
        ex is RedisCommandException ||
        (ex is RedisServerException &&
         ex.Message.Contains(StreamConstants.UnknownCommandErrorMessage, StringComparison.OrdinalIgnoreCase));

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
