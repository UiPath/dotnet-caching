using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public class CachingBuilder : ICachingBuilder
{
    private readonly List<Action<ICachingBuilder>> _callbacks = new();

    public CachingBuilder(IServiceCollection services, bool enabled = true)
    {
        Services = services;
        Enabled = enabled;
    }

    public IServiceCollection Services { get; private set; }

    public bool Enabled { get; private set; }

    public ICachingBuilder AddCache<T>(Func<IServiceProvider, T> cacheProvider)
        where T : class, ICache
    {
        Services.AddSingleton(sp => cacheProvider.Invoke(sp));
        return this;
    }

    public ICachingBuilder AddRegionCache<T>(Func<IServiceProvider, T> cacheProvider)
         where T : class, IRegionCache
    {
        Services.AddSingleton(sp => cacheProvider.Invoke(sp));
        return this;
    }

    internal void Complete()
    {
        if (!Enabled)
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
