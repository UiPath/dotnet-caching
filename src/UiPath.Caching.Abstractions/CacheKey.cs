using System.Globalization;

namespace UiPath.Caching;

public readonly struct CacheKey : IEquatable<CacheKey>
{
    /// <summary>Process-global casing for keys built without an explicit mode; seeded from <c>CacheOptions.KeyCasing</c>. Set only at startup.</summary>
    public static CacheKeyCasing DefaultCasing { get; set; } = CacheKeyCasing.Insensitive;

    public CacheKey()
    : this(string.Empty)
    {
    }

    public CacheKey(string? name)
    : this(name, DefaultCasing)
    {
    }

    public CacheKey(string? name, CacheKeyCasing casing)
    {
        Casing = casing;
        Name = casing == CacheKeyCasing.Insensitive
            ? name?.Trim().ToLowerInvariant() ?? string.Empty
            : name?.Trim() ?? string.Empty;
    }

    public string Name { get; }

    /// <summary>Normalization mode this key was built with; not part of equality.</summary>
    public CacheKeyCasing Casing { get; }

    /// <summary>New key from <paramref name="name"/>, preserving this key's casing mode.</summary>
    public CacheKey WithName(string? name) => new(name, Casing);

    public override bool Equals(object? obj) =>
        obj is CacheKey cacheKey && Equals(cacheKey);

    public bool Equals(CacheKey other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal);

    public bool IsNull => string.IsNullOrEmpty(Name);

    public override string ToString() =>
        Name;

    public override int GetHashCode() =>
        HashCode.Combine(Name, IsNull);

    public static implicit operator string(CacheKey cacheKey) =>
        cacheKey.Name;

    public static implicit operator CacheKey(string? cacheKey)
    {
        if (cacheKey == null) return default;
        return new CacheKey(cacheKey);
    }

    public static implicit operator CacheKey(int value) =>
        new(value.ToString(CultureInfo.InvariantCulture));

    public static implicit operator CacheKey(long value) =>
        new(value.ToString(CultureInfo.InvariantCulture));

    public static implicit operator CacheKey(Guid value) =>
        new(value.ToString());

    public static bool operator ==(CacheKey left, CacheKey right) =>
        left.Equals(right);

    public static bool operator !=(CacheKey left, CacheKey right) =>
        !(left == right);

    public static CacheKey Null { get; } = new CacheKey(null);
}
