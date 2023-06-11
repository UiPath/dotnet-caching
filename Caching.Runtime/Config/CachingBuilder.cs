using System.Text.Json;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public class CachingBuilder : ICachingBuilder
{
    private readonly List<Action<ICachingBuilder>> _callbacks = new();

    public CachingBuilder(IServiceCollection services, IConfiguration configuration)
    {
        Services = services;
        Configuration = configuration;
    }

    public IServiceCollection Services { get; private set; }

    public IConfiguration Configuration { get; private set; }

    public bool Enabled { get; set; } = true;

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
