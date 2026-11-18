namespace UiPath.Caching.Migration;

/// <summary>A cache name and a key as two fields, so no key text can run into the cache name.</summary>
internal readonly record struct LocalKey(string Cache, string Key)
{
    public int Hash() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Cache), StringComparer.Ordinal.GetHashCode(Key));
}
