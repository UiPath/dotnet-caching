namespace UiPath.Platform.Caching;

public interface IHybridCache : ICache
{
}

public interface IHybridCache<T> : ICache<T>
   where T : class
{
}
