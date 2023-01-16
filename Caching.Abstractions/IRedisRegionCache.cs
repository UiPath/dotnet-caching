namespace UiPath.Platform.Caching;

public interface IRedisRegionCache : IRegionCache
{
}

public interface IRedisRegionCache<T> : IRegionCache<T>
    where T : class
{
}
