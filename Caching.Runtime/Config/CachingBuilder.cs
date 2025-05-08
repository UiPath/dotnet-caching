using System.Text.Json;
using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public class CachingBuilder(IServiceCollection services, IConfiguration? configuration = null) : ICachingBuilder
{
    private readonly List<Action<ICachingBuilder>> _callbacks = [];

    public IServiceCollection Services { get; } = services;

    public IConfiguration Configuration { get; } = configuration ?? NullConfiguration.Instance;

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

        Services.TryAddSingleton<ISerializerProxy<RedisValue>>(sp => new SystemJsonSerializerProxy(sp.GetService<JsonSerializerOptions>()));
        Services.TryAddSingleton<IResiliencePipelineHolder>(ResiliencePipelineHolder.Empty);
        Services.TryAddSingleton<IChangeTokenFactory>(NullChangeTokenFactory.Instance);
        Services.TryAddSingleton<ITopicFactory>(NullTopicFactory.Instance);
        Services.TryAddSingleton<ICachingTelemetryProvider>(NullTelemetryProvider.Instance);
        Services.TryAddSingleton<ICacheEventFactory>(NullCacheEventFactory.Instance);
        Services.TryAddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.TryAddSingleton<IRedisProfiler>(NullRedisProfiler.Instance);
    }

    public void RegisterOnCompleteCallback(Action<ICachingBuilder> callback) =>
        _callbacks.Add(callback);
}
