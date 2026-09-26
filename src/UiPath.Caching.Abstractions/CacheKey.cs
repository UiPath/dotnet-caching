using System.Buffers;
using System.Globalization;

namespace UiPath.Caching;

public readonly struct CacheKey : IEquatable<CacheKey>
{
    /// <summary>Longest name normalized on the stack; a longer one rents a buffer.</summary>
    private const int StackBufferLength = 256;

    private static CacheKeyCasing _defaultCasing = CacheKeyCasing.Insensitive;

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

    /// <summary>Builds a key from text that is not yet a string, normalized as the string constructor normalizes it, into one new string.</summary>
    public CacheKey(ReadOnlySpan<char> name)
    : this(name, DefaultCasing)
    {
    }

    /// <inheritdoc cref="CacheKey(ReadOnlySpan{char})"/>
    public CacheKey(ReadOnlySpan<char> name, CacheKeyCasing casing)
    {
        Casing = casing;
        Name = casing switch
        {
            CacheKeyCasing.Insensitive or CacheKeyCasing.Sensitive => Normalize(name, casing),
            _ => throw new ArgumentOutOfRangeException(nameof(casing), casing, $"Unsupported {nameof(CacheKeyCasing)} value."),
        };
    }

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

    public static CacheKey Null { get; } = new CacheKey(null);

    public string Name { get; }

    /// <summary>Normalization mode this key was built with; not part of equality.</summary>
    public CacheKeyCasing Casing { get; }

    public bool IsNull => string.IsNullOrEmpty(Name);

    public static implicit operator string(CacheKey cacheKey) =>
        cacheKey.Name;

    public static implicit operator CacheKey(string? cacheKey)
    {
        if (cacheKey == null)
        {
            return default;
        }

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

    /// <summary>New key from <paramref name="name"/>, preserving this key's casing mode.</summary>
    public CacheKey WithName(string? name) => new(name, Casing);

    public override bool Equals(object? obj) =>
        obj is CacheKey cacheKey && Equals(cacheKey);

    public bool Equals(CacheKey other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override string ToString() =>
        Name;

    public override int GetHashCode() =>
        HashCode.Combine(Name, IsNull);

    /// <summary>Trims <paramref name="name"/>, lowercasing it for <see cref="CacheKeyCasing.Insensitive"/>, into <paramref name="destination"/>; false when it does not fit.</summary>
    internal static bool TryNormalize(ReadOnlySpan<char> name, Span<char> destination, CacheKeyCasing casing, out int written)
    {
        name = name.Trim();
        if (name.Length > destination.Length)
        {
            written = 0;
            return false;
        }

        if (casing == CacheKeyCasing.Sensitive)
        {
            name.CopyTo(destination);
            written = name.Length;
        }
        else
        {
            written = name.ToLowerInvariant(destination);
        }

        return true;
    }

    private static string Normalize(ReadOnlySpan<char> name, CacheKeyCasing casing)
    {
        name = name.Trim();
        if (name.IsEmpty)
        {
            return string.Empty;
        }

        char[]? rented = null;
        Span<char> buffer = name.Length <= StackBufferLength ? stackalloc char[StackBufferLength] : (rented = ArrayPool<char>.Shared.Rent(name.Length));
        TryNormalize(name, buffer, casing, out var written);
        var normalized = new string(buffer[..written]);
        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }

        return normalized;
    }
}
