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
}
