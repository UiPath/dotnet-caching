using System.Security.Cryptography;
using System.Text;

namespace UiPath.Caching;

/// <summary>Builds the batch <c>GetOrAdd</c> lock key for a set of keys; a single-key set is returned unchanged.</summary>
internal static class CompositeCacheKey
{
    private const string Prefix = "batch:";
    private const char Separator = '\u001F';

    internal static CacheKey For(IReadOnlyList<CacheKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            throw new ArgumentException("At least one cache key is required.", nameof(keys));
        }
        if (keys.Count == 1)
        {
            return keys[0];
        }

        var names = new string[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            names[i] = keys[i].Name;
        }
        Array.Sort(names, StringComparer.Ordinal);

        var builder = new StringBuilder();
        foreach (var name in names)
        {
            builder.Append(name).Append(Separator);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Prefix + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }
}
