namespace UiPath.Caching.Redis;

internal interface IInvalidationSink
{
    /// <summary>Told that another node changed or removed this key.</summary>
    void Invalidated(CacheKey key);

    /// <summary>Told that events were lost, so any key may have changed.</summary>
    void InvalidatedAll();
}
