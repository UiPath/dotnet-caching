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
            var options = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
            CachePolicy? defaultPolicy = options.DefaultCachePolicy;
            var builders = sp.GetServices<ICachePolicyDefaultBuilder>().ToArray();
            // Validate against every registered provider, not just the DefaultCache match — a named
            // policy is resolved by Cache<T> type name and may be used against any provider's cache
            // instance. Runs here (not via IValidateOptions) to avoid a DI cycle through
            // MultilayerCacheLockCrossOptionsValidator → IOptions<CacheOptions>.
            foreach (var builder in builders)
            {
                var providerDefault = builder.Build();
                var effectiveDefaultLock = CachePolicyMerger.MergeLock(options.DefaultCachePolicy?.Lock, providerDefault?.Lock);
                if (effectiveDefaultLock is not null)
                {
                    CachePolicyLockValidator.ValidateEffectiveDefaultAndNamedLocks(options, effectiveDefaultLock);
                }
            }
            return new DefaultCachePolicyFactory(options.Policies, defaultPolicy);
        });
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CacheOptions>, CachePolicyLockValidator>());
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CacheOptions>, CachePolicyRehydrateValidator>());
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
