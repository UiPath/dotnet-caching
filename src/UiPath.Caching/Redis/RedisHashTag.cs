namespace UiPath.Caching.Redis;

internal static class RedisHashTag
{
    /// <summary>Non-empty content between the first '{' and the next '}' is what Redis Cluster hashes.</summary>
    public static bool HasValidTag(string key)
    {
        var open = key.IndexOf('{');
        if (open < 0)
        {
            return false;
        }
        var close = key.IndexOf('}', open + 1);
        return close > open + 1;
    }

    public static bool ContainsNoBraces(string key) =>
        key.IndexOf('{') < 0 && key.IndexOf('}') < 0;
}
