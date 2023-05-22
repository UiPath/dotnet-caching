using System.Text.Json;

namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public class CachingBuilder : ICachingBuilder
{
    private readonly List<Action<ICachingBuilder>> _callbacks = new();

    public CachingBuilder(IServiceCollection services, IConfigurationSection configuration)
    {
        Services = services;
        Configuration = configuration;
    }

    public IServiceCollection Services { get; private set; }

    public IConfigurationSection Configuration { get; private set; }

    public bool Enabled { get; set; } = true;

    public ICachingBuilder AddCache<T>(Func<IServiceProvider, T> cacheProvider)
        where T : class, ICache
    {
        Services.TryAddSingleton(sp => cacheProvider.Invoke(sp));
        return this;
    }

    public ICachingBuilder AddRegionCache<T>(Func<IServiceProvider, T> cacheProvider)
         where T : class, IRegionCache
    {
        Services.TryAddSingleton(sp => cacheProvider.Invoke(sp));
        return this;
    }

    internal void Complete()
    {
        if(!Enabled)
        {
            return;
        }

        foreach (var callback in _callbacks)
        {
            callback(this);
        }

        Services.TryAddSingleton<ISerializerProxy>(sp => new SystemJsonSerializerProxy(sp.GetService<JsonSerializerOptions>()));
        Services.TryAddSingleton<IPolicyHolder>(sp => new PolicyHolder(new NoOpExecutor()));
    }

    public void RegisterOnCompleteCallback(Action<ICachingBuilder> callback) =>
        _callbacks.Add(callback);
}
