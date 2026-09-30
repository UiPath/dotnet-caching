namespace UiPath.Caching;

/// <summary>Cached <see cref="CacheKey"/> equality comparers: <see cref="Sensitive"/> (ordinal, the struct's own equality) and <see cref="Insensitive"/> (ordinal, case-folding).</summary>
public abstract class CacheKeyComparer : EqualityComparer<CacheKey>
{
    public static CacheKeyComparer Sensitive { get; } = new SensitiveComparer();

    public static CacheKeyComparer Insensitive { get; } = new InsensitiveComparer();

    private sealed class SensitiveComparer : CacheKeyComparer
    {
        public override bool Equals(CacheKey x, CacheKey y) => x.Equals(y);

        public override int GetHashCode(CacheKey obj) => obj.GetHashCode();
    }

    private sealed class InsensitiveComparer : CacheKeyComparer
    {
        public override bool Equals(CacheKey x, CacheKey y) =>
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode(CacheKey obj) =>
            obj.Name is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
    }
}
