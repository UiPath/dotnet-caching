using System.Text.Json;
using UiPath.Caching.Locking;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Config;

[ExcludeFromCodeCoverage]
public class CachingBuilder(IServiceCollection services, IConfiguration? configuration = null) : ICachingBuilder
{
    private readonly List<Action<ICachingBuilder>> _callbacks = [];
    private readonly HashSet<object> _registeredKeys = [];

    public IServiceCollection Services { get; } = services;

    public IConfiguration Configuration { get; } = configuration ?? NullConfiguration.Instance;

    public bool Enabled { get; set; } = true;

    public void RegisterOnCompleteCallback(object key, Action<ICachingBuilder> callback)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(callback);

        if (_registeredKeys.Add(key))
        {
            _callbacks.Add(callback);
        }
    }

    internal void Complete()
    {
        // Before the switch: a keyspace collision is a configuration error, not a runtime condition.
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CacheOptions>, ReservedRedisKeyspaceValidator>());

        if (Enabled)
        {
            foreach (var callback in _callbacks)
            {
                callback(this);
            }
        }

        // After the callbacks, which may register services, and before the switch: a disabled profile is
        // usually the test copy of one that is on, and should report the same dead registration.
        ThrowIfLegacySerializerRegistered();

        if(!Enabled)
        {
            return;
        }

        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CacheOptions>, CacheKeyCasingSeeder>());
        Services.TryAddSingleton<ISerializerProxy<byte[]>>(sp => new SystemJsonByteSerializerProxy(sp.GetService<JsonSerializerOptions>()));
        Services.TryAddSingleton<IResiliencePipelineProvider>(EmptyResiliencePipelineProvider.Instance);
        Services.TryAddSingleton<IChangeTokenFactory>(NullChangeTokenFactory.Instance);
        Services.TryAddSingleton<ITopicFactory>(NullTopicFactory.Instance);
        Services.TryAddSingleton<ICachingTelemetryProvider>(NullTelemetryProvider.Instance);
        Services.TryAddSingleton<IKeyMaskingPolicy>(NullKeyMaskingPolicy.Instance);
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

    private void ThrowIfLegacySerializerRegistered()
    {
        if (Services.Any(d => d.ServiceType == typeof(ISerializerProxy<RedisValue>)))
        {
            throw new InvalidOperationException(
                $"A registration for ISerializerProxy<RedisValue> is present, but that seam no longer exists and " +
                $"nothing resolves it — the serializer would be silently ignored. Re-register the implementation " +
                $"as ISerializerProxy<byte[]> (see docs/how-to/extending.md#custom-serializer). Note that " +
                $"{nameof(SystemJsonByteSerializerProxy)} keeps the wire format unchanged, while " +
                $"{nameof(RawByteSerializerProxy)} stores byte payloads verbatim.");
        }
    }
}
