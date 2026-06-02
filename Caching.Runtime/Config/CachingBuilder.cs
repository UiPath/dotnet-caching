using System.Text.Json;
using UiPath.Platform.Caching.Locking;
using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public class CachingBuilder(IServiceCollection services, IConfiguration? configuration = null) : ICachingBuilder
{
    private readonly List<Action<ICachingBuilder>> _callbacks = [];
    private readonly HashSet<object> _registeredKeys = [];

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
        Services.TryAddSingleton<ILocalLock>(NullLocalLock.Instance);
        Services.TryAddSingleton<IDistributedLock>(NullDistributedLock.Instance);
        Services.TryAddSingleton<ICachePolicyFactory>(sp =>
        {
            var resolvedOptions = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
            return new DefaultCachePolicyFactory(
                resolvedOptions.Policies,
                resolvedOptions.DefaultCachePolicy,
                resolvedOptions.DistributedLockPollInterval);
        });
    }

    public void RegisterOnCompleteCallback(object key, Action<ICachingBuilder> callback)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(callback);

        if (_registeredKeys.Add(key))
        {
            _callbacks.Add(callback);
        }
    }
}
