namespace UiPath.Platform.Caching.Redis;
internal static class RedisUtils
{
    private static readonly Version ExpireTimeVersion = new(7, 0);

    internal static bool SupportsExpireTime(Version version) => version >= ExpireTimeVersion;
}
