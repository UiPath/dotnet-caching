namespace UiPath.Platform.Caching.Redis;

public static class CacheUtils
{
    public static string GetKey(string key, string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentNullException(nameof(key));
        }

        return (string.IsNullOrWhiteSpace(instanceName) ? key : string.Join(CacheConstants.KeySeparator, instanceName, key)).ToLowerInvariant();
    }
}
