namespace UiPath.Platform.Caching;

public readonly struct CacheKey : IEquatable<CacheKey>
{
    public CacheKey()
    : this(string.Empty)
    {
    }

    public CacheKey(string? name) =>
        Name = name?.Trim().ToLowerInvariant() ?? string.Empty;

    public string Name { get; }

    public override bool Equals(object? obj) =>
        obj is CacheKey cacheKey && Equals(cacheKey);

    public bool Equals(CacheKey other) =>
        string.Equals(Name, other.Name, StringComparison.InvariantCultureIgnoreCase);

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

    public static bool operator ==(CacheKey left, CacheKey right) =>
        left.Equals(right);

    public static bool operator !=(CacheKey left, CacheKey right) =>
        !(left == right);

    public static CacheKey Null { get; } = new CacheKey(null);
}
