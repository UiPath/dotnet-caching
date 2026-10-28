namespace UiPath.Caching;

[ExcludeFromCodeCoverage]
internal static class NullResults
{
    internal static ValueTask<bool> Contains<T>()
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(false);
    }

    internal static ValueTask<DateTimeOffset?> ExpireTime<T>()
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(default(DateTimeOffset?));
    }
}
