using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Polly;
using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public class CachingBuilder : ICachingBuilder
{
    private readonly List<Action<ICachingBuilder>> _callbacks = new();
    private readonly List<IAsyncPolicy> _policies = new();
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

    public ICachingBuilder AddPolicy(IAsyncPolicy policy)
    {
        _policies.Add(policy);
        return this;
    }

    internal void Complete()
    {
        if (!Enabled)
        {
            return;
        }

        Services.TryAddSingleton<ISerializerProxy>(sp => new SystemJsonSerializerProxy(sp.GetService<JsonSerializerOptions>()));
        var policies = _policies.ToArray();
        if (policies.Length != 0)
        {
            Services.TryAddSingleton<IPolicyHolder>(new PolicyHolder(policies));
        }
        else
        {
            Services.TryAddSingleton<IPolicyHolder>(sp => new PolicyHolder(new[] { sp.BuildCircuitBreakerPolicy(), sp.BuildTimeoutPolicy() }));
        }

        foreach (var callback in _callbacks)
        {
            callback(this);
        }
    }

    public void RegisterOnCompleteCallback(Action<ICachingBuilder> callback) =>
        _callbacks.Add(callback);
}
