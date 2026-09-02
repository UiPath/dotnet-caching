using System.Globalization;

namespace UiPath.Caching;

public readonly struct CacheKey : IEquatable<CacheKey>
{
    private static CacheKeyCasing _defaultCasing = CacheKeyCasing.Insensitive;

    /// <summary>
    /// Process-global casing for keys built without an explicit mode; seeded from <c>CacheOptions.KeyCasing</c>.
    /// Set only at startup. Rejects a value outside the enum on assignment rather than at the next key built,
    /// since this is global state and the throw would otherwise surface far from the assignment that caused it.
    /// </summary>
    public static CacheKeyCasing DefaultCasing
    {
        get => _defaultCasing;
        set => _defaultCasing = value is CacheKeyCasing.Insensitive or CacheKeyCasing.Sensitive
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Unsupported {nameof(CacheKeyCasing)} value.");
    }

    public CacheKey()
    : this(string.Empty)
    {
    }

    public CacheKey(string? name)
    : this(name, DefaultCasing)
    {
    }

    /// <summary>
    /// Builds a key, trimming the name and lowercasing it when <paramref name="casing"/> is
    /// <see cref="CacheKeyCasing.Insensitive"/>. An unrecognized value is rejected rather than treated as
    /// sensitive, which would silently stop lowercasing and relocate every key built with it.
    /// </summary>
    public CacheKey(string? name, CacheKeyCasing casing)
    {
        Casing = casing;
        Name = casing switch
        {
            CacheKeyCasing.Insensitive => name?.Trim().ToLowerInvariant() ?? string.Empty,
            CacheKeyCasing.Sensitive => name?.Trim() ?? string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(casing), casing, $"Unsupported {nameof(CacheKeyCasing)} value."),
        };
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
