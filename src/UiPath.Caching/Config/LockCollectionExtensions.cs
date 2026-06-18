using UiPath.Caching.Locking;

namespace UiPath.Caching.Config;

[ExcludeFromCodeCoverage]
public static class LockCollectionExtensions
{
    public static ICachingBuilder AddLocalLock(this ICachingBuilder builder)
    {
        builder.Services.TryAddSingleton<ILocalLock, AsyncKeyedLocalLock>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CacheOptions>, CacheOptionsLockValidator>());
        return builder;
    }

    public static ICachingBuilder AddRedisDistributedLock(this ICachingBuilder builder)
    {
        builder.Services.TryAddSingleton<IDistributedLock, RedisDistributedLock>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CacheOptions>, CacheOptionsLockValidator>());
        return builder;
    }
}
