using System.Globalization;

namespace UiPath.Caching;

/// <summary>How a cache masks keys in its log lines, from <see cref="ICacheOptions.MaskKeys"/> and <see cref="ICacheOptions.MaskedKeyPrefixes"/>.</summary>
internal sealed class KeyMasking
{
    private const int Revealed = 3;
    private const string MaskText = "****";
    private const string Probe = "loggedkeyprobe";

    public static readonly KeyMasking Off = new(enabled: false, []);

    private readonly bool _enabled;
    private readonly string[] _prefixes;

    public KeyMasking(bool enabled, IReadOnlyList<string> prefixes)
    {
        _enabled = enabled;
        _prefixes = [.. prefixes];
    }

    public static KeyMasking For(ICacheOptions options) => options.MaskKeys ? new(enabled: true, options.MaskedKeyPrefixes) : Off;

    /// <summary>
    /// <paramref name="layerPrefix"/> is what this layer's own strategy composed in front of the cache key (the Redis
    /// prefix, say); it is kept and the cache key behind it is judged. A number or a GUID is an identifier, not a
    /// secret, and stays. Anything else is masked when it starts with a listed prefix, keeping that prefix:
    /// <c>myapp:s:session:cos****</c>.
    /// </summary>
    public string Render(string key, string? layerPrefix)
    {
        if (!_enabled)
        {
            return key;
        }

        var head = layerPrefix is not null && key.Length > layerPrefix.Length && key.StartsWith(layerPrefix, StringComparison.OrdinalIgnoreCase)
            ? layerPrefix.Length
            : 0;
        var cacheKey = key.AsSpan(head);
        if (IsIdentifier(cacheKey))
        {
            return key;
        }

        foreach (var prefix in _prefixes)
        {
            if (cacheKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Concat(key.AsSpan(0, head + prefix.Length), MaskValue(cacheKey[prefix.Length..]));
            }
        }

        return key;
    }

    /// <summary>The first three characters, then <c>****</c>; used as is for values known to be secrets.</summary>
    public static string MaskValue(ReadOnlySpan<char> value) =>
        value.Length > Revealed ? string.Concat(value[..Revealed], MaskText) : MaskText;

    /// <summary>What the Redis key strategy puts in front of a key, learnt from a probe; null when the key is not the tail of what it composes.</summary>
    public static string? PrefixOf(IRedisKeyStrategy strategy)
    {
        var composed = (string?)strategy.GetRedisKey(new CacheKey(Probe, CacheKeyCasing.Sensitive));
        return composed is not null && composed.Length > Probe.Length && composed.EndsWith(Probe, StringComparison.OrdinalIgnoreCase)
            ? composed[..^Probe.Length]
            : null;
    }

    /// <summary>What the cache key strategy puts in front of a key; the distributed adapter lists it as a masked prefix on its tier.</summary>
    public static string? PrefixOf(ICacheKeyStrategy strategy)
    {
        var composed = strategy.GetCacheKey<object>(new CacheKey(Probe, CacheKeyCasing.Sensitive)).Name;
        return composed.Length > Probe.Length && composed.EndsWith(Probe, StringComparison.OrdinalIgnoreCase)
            ? composed[..^Probe.Length]
            : null;
    }

    public static bool IsIdentifier(ReadOnlySpan<char> value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) || Guid.TryParse(value, out _);
}

/// <summary>A key as a log line should show it, rendered only when the logger formats the line.</summary>
internal readonly struct LoggedKey
{
    private readonly string _key;
    private readonly string? _layerPrefix;
    private readonly KeyMasking _masking;

    public LoggedKey(string key, string? layerPrefix, KeyMasking masking)
    {
        _key = key;
        _layerPrefix = layerPrefix;
        _masking = masking;
    }

    public override string ToString() => _masking.Render(_key, _layerPrefix);

    public static string Join(IEnumerable<CacheKey> keys, KeyMasking masking) =>
        string.Join(",", keys.Select(key => masking.Render(key.Name, layerPrefix: null)));
}
