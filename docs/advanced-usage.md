# Advanced usage

Using Multiple cache providers in the same time and multiple cache key strategies (with simple prefix or with application version prefix)

```csharp

public static class CacheFactoryExtensions
{
    private static const char Separator = ':';
    private static readonly string ApplicationVersion = "1.0";

    private static readonly ICacheKeyStrategy AppKey1Strategy = AppVersionPrefix("key1");
    private static readonly ICacheKeyStrategy Key2Strategy = Prefix("key2");

    public static IHashCache<MyDto> MyDtos(this ICacheFactory factory) =>
        new HashCache<MyDto>(factory.CreateMultilayerHashCache(), AppKey1Strategy);

    public static ICache<List<string>> SomeLists(this ICacheFactory factory) =>
        new Cache<List<string>>(factory.CreateRedisCache(), Key2Strategy);

    private static ICacheKeyStrategy AppVersionPrefix(string prefix) =>
        new PrefixCacheKeyStrategy(string.Join(Separator, prefix, ApplicationVersion), Separator);

    private static ICacheKeyStrategy Prefix(string prefix) =>
        new PrefixCacheKeyStrategy(string.Join(Separator, prefix), Separator);
}

public class MyService{
    private IHashCache<MyDto> _cache;

    public MyService(ICacheFactory cacheFactory){
        _cache = cacheFactory.MyDtos();
    }
}
```

Extending the library:

* cache provider. Implement [ICacheProvider](/Caching.Abstractions/ICacheProvider.cs) and register it in `ICacheFactory.AddProvider(ICacheProvider provider)`
* topic provider for event broadcasting. Implement [ITopicProvider](/Caching.Abstractions/Broadcast/ITopicProvider.cs) and register it in `ITopicFactory.AddProvider(ITopicProvider provider)`