namespace UiPath.Platform.Caching.Redis;
internal static class RedisUtils
{
    internal static bool SupportsExpireTime(int version) => version >= 7;
}
