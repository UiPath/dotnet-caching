namespace UiPath.Platform.Caching;

public interface IMemCache : ICache
{
}

public interface IMemCache<T> : ICache<T>
   where T : class
{
}
