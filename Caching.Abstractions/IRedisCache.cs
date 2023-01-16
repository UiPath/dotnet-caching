namespace UiPath.Platform.Caching;

public interface IRedisCache : ICache
{
}

public interface IRedisCache<T> : ICache<T>
    where T : class
{
}


