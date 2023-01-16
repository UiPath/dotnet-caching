using Microsoft.Extensions.DependencyInjection;
using Polly;

namespace UiPath.Platform.Caching.Config;

public interface ICachingBuilder
{
    IServiceCollection Services { get; }

    public bool Enabled { get; }

    void RegisterOnCompleteCallback(Action<ICachingBuilder> callback);

    ICachingBuilder AddCache<TCache>(Func<IServiceProvider, TCache> cacheProvider)
        where TCache : class, ICache;

    ICachingBuilder AddRegionCache<TCache>(Func<IServiceProvider, TCache> cacheProvider)
    where TCache : class, IRegionCache;

    ICachingBuilder AddPolicy(IAsyncPolicy policy);
}
