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

    /// <summary>The tag <paramref name="key"/> already carries, kept as is, or the whole key wrapped as one when it carries none.</summary>
    public static string EnsureTag(string key, string purpose)
    {
        if (HasValidTag(key))
        {
            return key;
        }
        if (key.Length > 0 && ContainsNoBraces(key))
        {
            return "{" + key + "}";
        }
        // Wrapping here would pair the added '{' with the brace already inside, leaving a tag that is only a
        // prefix of the key and collapsing unrelated keys onto one slot.
        throw new InvalidOperationException(
            $"{purpose} cannot guarantee Redis Cluster slot affinity for key '{key}'. " +
            "The key must either contain a valid hash tag (non-empty content between '{' and '}', e.g. 'app:st:{topic}') " +
            "or be non-empty and contain no '{' or '}' characters at all.");
    }
}
