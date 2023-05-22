using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace UiPath.Platform.Caching.Config;

public interface ICachingBuilder
{
    IServiceCollection Services { get; }

    IConfigurationSection Configuration { get; }

    bool Enabled { get; set; }

    void RegisterOnCompleteCallback(Action<ICachingBuilder> callback);

    ICachingBuilder AddCache<TCache>(Func<IServiceProvider, TCache> cacheProvider)
        where TCache : class, ICache;

    ICachingBuilder AddRegionCache<TCache>(Func<IServiceProvider, TCache> cacheProvider)
    where TCache : class, IRegionCache;
}
