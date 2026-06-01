namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class CachingBuilderFactoryExtensions
{
    public static ICachingBuilder UseCacheFactory<T>(this ICachingBuilder builder) where T : class, ICacheFactory
    {
        if (!builder.Enabled)
        {
            return builder;
        }
        builder.Services.Replace(ServiceDescriptor.Singleton<ICacheFactory, T>());
        return builder;
    }

    public static ICachingBuilder UseCacheFactory(this ICachingBuilder builder, ICacheFactory instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!builder.Enabled)
        {
            return builder;
        }
        builder.Services.Replace(ServiceDescriptor.Singleton(instance));
        return builder;
    }

    public static ICachingBuilder UseCacheFactory(this ICachingBuilder builder, Func<IServiceProvider, ICacheFactory> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!builder.Enabled)
        {
            return builder;
        }
        builder.Services.Replace(ServiceDescriptor.Singleton(factory));
        return builder;
    }

    public static ICachingBuilder UseCachePolicyFactory<T>(this ICachingBuilder builder) where T : class, ICachePolicyFactory
    {
        if (!builder.Enabled)
        {
            return builder;
        }
        builder.Services.Replace(ServiceDescriptor.Singleton<ICachePolicyFactory, T>());
        return builder;
    }

    public static ICachingBuilder UseCachePolicyFactory(this ICachingBuilder builder, ICachePolicyFactory instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!builder.Enabled)
        {
            return builder;
        }
        builder.Services.Replace(ServiceDescriptor.Singleton(instance));
        return builder;
    }

    public static ICachingBuilder UseCachePolicyFactory(this ICachingBuilder builder, Func<IServiceProvider, ICachePolicyFactory> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!builder.Enabled)
        {
            return builder;
        }
        builder.Services.Replace(ServiceDescriptor.Singleton(factory));
        return builder;
    }
}
