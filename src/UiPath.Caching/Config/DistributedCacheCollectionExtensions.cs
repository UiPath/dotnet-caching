using Microsoft.Extensions.Caching.Distributed;
using UiPath.Caching.Distributed;
using UiPath.Caching.Locking;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Config;

public static class DistributedCacheCollectionExtensions
{
    /// <summary>Service key of the distributed cache's private <see cref="ICacheProvider"/> and <see cref="IHashCache"/> registrations.</summary>
    public const string DistributedCacheServiceKey = "UiPath.Caching.Distributed";

    /// <summary>
    /// The application's own caches fill the differentiator slot with a <see cref="RedisTypePrefixes"/>
    /// value, so a distributed differentiator matching one of them would merge the two keyspaces. Only this
    /// assembly's prefixes are listed: a package layered on top of it defines its own, which this assembly
    /// cannot see without depending on it the wrong way round.
    /// </summary>
    private static readonly string[] ApplicationTypePrefixes =
    [
        RedisTypePrefixes.String,
        RedisTypePrefixes.Hash,
        RedisTypePrefixes.PubSub,
        RedisTypePrefixes.Streams,
    ];

    /// <summary>Registers an <see cref="IDistributedCache"/> backed by a dedicated case-sensitive cache instance in its own keyspace. <paramref name="providerName"/> selects the backing tier: Redis (recommended), InMemoryRedis, or InMemory.</summary>
    public static ICachingBuilder AddDistributedCache(
        this ICachingBuilder builder,
        string providerName,
        Action<UiPathDistributedCacheOptions>? configure = null)
    {
        Guard.NotNullOrWhiteSpace(providerName, nameof(providerName));
        var options = new UiPathDistributedCacheOptions();
        configure?.Invoke(options);
        EnsureKeyspaceIsolation(options);

        if (builder.Services.Any(d => d.IsKeyedService
            && Equals(d.ServiceKey, DistributedCacheServiceKey)
            && d.ServiceType == typeof(IHashCache)))
        {
            throw new InvalidOperationException(
                "AddDistributedCache has already been called. Registering it twice would build the adapter from the first call's options over the second call's backing tier; call it once.");
        }

        if (!builder.Enabled)
        {
            RegisterNullAdapter(builder);
            return builder;
        }

        if (providerName is KnownCacheProviderNames.InMemory or KnownCacheProviderNames.InMemoryRedis)
        {
            builder.Services.TryAddMemoryCacheFactory();
        }

        builder.Services.AddKeyedSingleton<ICacheProvider>(DistributedCacheServiceKey,
            (sp, _) => CreateProvider(sp, providerName, options));
        builder.Services.AddKeyedSingleton<IHashCache>(DistributedCacheServiceKey,
            (sp, key) => CreateHashCache(sp, key!, providerName));

        RegisterAdapter(builder.Services, ServiceDescriptor.Singleton<IDistributedCache>(sp => new UiPathDistributedCache(
            sp.GetRequiredKeyedService<IHashCache>(DistributedCacheServiceKey),
            options,
            ResolveCacheKeyStrategy(options),
            ResolvePolicy(sp, options),
            sp.GetRequiredService<ILoggerFactory>().Create<UiPathDistributedCache>(),
            sp.GetService<ISystemClock>(),
            slideByRewrite: providerName == KnownCacheProviderNames.InMemory)));

        return builder;
    }

    /// <summary>Satisfies <see cref="IDistributedCache"/> when caching is switched off, so <c>AddSession()</c> and DataProtection resolve instead of failing at startup.</summary>
    private static void RegisterNullAdapter(ICachingBuilder builder) =>
        builder.Services.Replace(ServiceDescriptor.Singleton<IDistributedCache>(NullDistributedCache.Instance));

    /// <summary>Replace rather than TryAdd, so a host that called <c>AddDistributedMemoryCache()</c> first cannot silently win and turn this registration into a no-op.</summary>
    private static void RegisterAdapter(IServiceCollection services, ServiceDescriptor adapter) =>
        services.Replace(adapter);

    /// <summary>
    /// Honors the tier's own <c>Enabled</c> switch the way <see cref="CacheFactory"/> does. The switch is read
    /// from the tier's options before the provider is resolved, so a disabled tier yields a null cache instead
    /// of first demanding the infrastructure that provider would need.
    /// </summary>
    private static IHashCache CreateHashCache(IServiceProvider sp, object serviceKey, string providerName)
    {
        if (!IsTierEnabled(sp, providerName))
        {
            return NullHashCache.Instance;
        }

        var provider = sp.GetRequiredKeyedService<ICacheProvider>(serviceKey);
        return provider.Enabled ? provider.CreateHashCache() : NullHashCache.Instance;
    }

    /// <summary>An unrecognized name resolves the provider anyway, so the unsupported-tier error still surfaces.</summary>
    private static bool IsTierEnabled(IServiceProvider sp, string providerName) =>
        providerName switch
        {
            KnownCacheProviderNames.Redis => sp.GetRequiredService<IOptions<RedisCacheOptions>>().Value.Enabled,
            KnownCacheProviderNames.InMemoryRedis => sp.GetRequiredService<IOptions<InMemoryRedisCacheOptions>>().Value.Enabled,
            KnownCacheProviderNames.InMemory => sp.GetRequiredService<IOptions<InMemoryCacheOptions>>().Value.Enabled,
            _ => true,
        };

    /// <summary>
    /// The multilayer tier keeps entries in a local layer that only broadcast invalidates across nodes, so
    /// without effective broadcast a node serves stale sessions indefinitely. Every switch that can turn it off
    /// is checked, not just the registration: a registered <c>ChangeTokenFactory</c> is replaced by the null one
    /// inside <see cref="InMemoryRedisCacheProvider"/> when its own <c>BroadcastEnable</c> is false, and
    /// <c>TopicFactory</c> hands out a null provider when <see cref="CacheOptions.BroadcastEnabled"/> is false —
    /// either leaves the wiring present but inert. Checked when the provider is resolved rather than from a
    /// completion callback, because <c>AddInMemoryRedis</c> installs its wiring from one and callbacks run in
    /// registration order, which would make this depend on call order.
    /// </summary>
    private static void RequireTierPrerequisites(IServiceProvider sp, string providerName)
    {
        if (providerName != KnownCacheProviderNames.InMemoryRedis)
        {
            return;
        }

        if (sp.GetService<IChangeTokenFactory>() is null or NullChangeTokenFactory)
        {
            throw new InvalidOperationException(
                $"AddDistributedCache('{KnownCacheProviderNames.InMemoryRedis}') requires the broadcast wiring installed by AddInMemoryRedis; without it the local cache layer is never invalidated across nodes. Call AddInMemoryRedis on the caching builder, or use the Redis tier.");
        }

        if (!sp.GetRequiredService<IOptions<InMemoryRedisCacheOptions>>().Value.BroadcastEnable)
        {
            throw new InvalidOperationException(
                $"AddDistributedCache('{KnownCacheProviderNames.InMemoryRedis}') requires broadcast, but InMemoryRedisCacheOptions.BroadcastEnable is false, which replaces the provider's change-token, topic and event factories with null ones. The local layer would never be invalidated across nodes, so a node would serve stale entries indefinitely. Leave it enabled, or use the Redis tier.");
        }

        if (!sp.GetRequiredService<IOptions<CacheOptions>>().Value.BroadcastEnabled)
        {
            throw new InvalidOperationException(
                $"AddDistributedCache('{KnownCacheProviderNames.InMemoryRedis}') requires broadcast, but CacheOptions.BroadcastEnabled is false, so TopicFactory hands out a null topic provider and the local layer is never invalidated across nodes. Leave it enabled, or use the Redis tier.");
        }
    }

    /// <summary>Null keeps the default prefix strategy, which is what separates distributed keys from the application's.</summary>
    private static ICacheKeyStrategy ResolveCacheKeyStrategy(UiPathDistributedCacheOptions options) =>
        options.CacheKeyStrategy ?? new PrefixCacheKeyStrategy(UiPathDistributedCacheOptions.DefaultKeyPrefix);

    private static string ResolveRedisKeyDifferentiator(UiPathDistributedCacheOptions options) =>
        options.RedisKeyDifferentiator ?? UiPathDistributedCacheOptions.DefaultRedisKeyDifferentiator;

    /// <summary>
    /// Both keyspace seams are overridable, so reject values that would merge them back into the application's.
    /// The prefix comparison ignores case because <see cref="PrefixRedisKeyStrategy"/> lowercases what it is
    /// given: <c>"H"</c> composes the application's <c>"h"</c> keyspace, which an ordinal match would wave through.
    /// </summary>
    private static void EnsureKeyspaceIsolation(UiPathDistributedCacheOptions options)
    {
        if (options.RedisKeyDifferentiator is not { } differentiator)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(differentiator))
        {
            throw new InvalidOperationException(
                "UiPathDistributedCacheOptions.RedisKeyDifferentiator must be a non-empty value, or null to use the default.");
        }

        if (ApplicationTypePrefixes.Contains(differentiator, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"UiPathDistributedCacheOptions.RedisKeyDifferentiator '{differentiator}' is one of the type prefixes the application's own caches use, which would put the distributed cache in the same Redis keyspace. Choose another value.");
        }
    }

    private static CachePolicy? ResolvePolicy(IServiceProvider sp, UiPathDistributedCacheOptions options)
    {
        if (options.PolicyName is not { } policyName)
        {
            return null;
        }

        var policy = sp.GetService<ICachePolicyFactory>()?.Resolve(policyName);
        return policy ?? throw new InvalidOperationException(
            $"Cache policy '{policyName}' configured on UiPathDistributedCacheOptions.PolicyName is not registered in CacheOptions.Policies.");
    }

    private static ICacheProvider CreateProvider(IServiceProvider sp, string providerName, UiPathDistributedCacheOptions options)
    {
        RequireTierPrerequisites(sp, providerName);
        var provider = CreateProviderCore(sp, providerName, options, ResolveRedisKeyDifferentiator(options));
        EnsureBoundedWrites(sp, providerName, options);
        return provider;
    }

    /// <summary>
    /// Writes without a caller expiration take a default TTL; reject configurations where none resolves, or
    /// resolves non-positive, and unbounded entries are not allowed. The fallback is resolved in the order the
    /// backing cache applies it, which differs by case: a named policy passed per-operation beats the tier
    /// default (<see cref="MultilayerCacheBase"/>), but with no named policy the cache builds its default from
    /// the tier default as the primary and the factory default as the fallback
    /// (<see cref="CachePolicyMerger"/>), so the tier default wins there.
    /// </summary>
    private static void EnsureBoundedWrites(IServiceProvider sp, string providerName, UiPathDistributedCacheOptions options)
    {
        if (options.AllowUnboundedEntries && options.DefaultEntryExpiration is not null)
        {
            throw new InvalidOperationException(
                "UiPathDistributedCacheOptions.AllowUnboundedEntries and DefaultEntryExpiration both describe what a write with no caller expiration should do; set one or the other.");
        }

        if (options.DefaultEntryExpiration is { } configured && configured <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"UiPathDistributedCacheOptions.DefaultEntryExpiration must be positive, but was {configured}. A non-positive default would make writes without a caller expiration expire immediately.");
        }

        if (options.AllowUnboundedEntries || options.DefaultEntryExpiration is not null)
        {
            return;
        }

        TimeSpan? tierDefault = providerName switch
        {
            KnownCacheProviderNames.Redis => sp.GetRequiredService<IOptions<RedisCacheOptions>>().Value.DefaultExpiration,
            KnownCacheProviderNames.InMemoryRedis => sp.GetRequiredService<IOptions<InMemoryRedisCacheOptions>>().Value.DefaultExpiration,
            _ => sp.GetRequiredService<IOptions<InMemoryCacheOptions>>().Value.DefaultExpiration,
        };
        var fallback = ResolvePolicy(sp, options) is { } named
            ? named.DistributedExpiration ?? tierDefault
            : tierDefault ?? sp.GetService<ICachePolicyFactory>()?.Default?.DistributedExpiration;

        if (fallback is { } resolved)
        {
            if (resolved <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"AddDistributedCache('{providerName}') resolved {resolved} as the expiration for writes without a caller expiration, so they would expire immediately. Correct the provider's DefaultExpiration or the policy's DistributedExpiration, or set UiPathDistributedCacheOptions.DefaultEntryExpiration.");
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"AddDistributedCache('{providerName}') would store entries without an expiration: the provider's DefaultExpiration is null and no cache policy supplies DistributedExpiration. Set UiPathDistributedCacheOptions.DefaultEntryExpiration, configure the provider's DefaultExpiration, or set AllowUnboundedEntries.");
        }
    }

    private static ICacheProvider CreateProviderCore(
        IServiceProvider sp, string providerName, UiPathDistributedCacheOptions options, string differentiator) =>
        providerName switch
        {
            KnownCacheProviderNames.Redis => CreateRedisProvider(sp, options, differentiator),
            KnownCacheProviderNames.InMemoryRedis => new InMemoryRedisCacheProvider(
                Options.Create(WithNeutralCacheKeyStrategy(sp.GetRequiredService<IOptions<InMemoryRedisCacheOptions>>().Value)),
                sp.GetRequiredService<IOptions<CacheOptions>>(),
                sp.GetRequiredService<IMemoryCacheFactory>(),
                () => CreatePrivateRedisFactory(sp, options, differentiator),
                sp.GetRequiredService<IChangeTokenFactory>(),
                sp.GetRequiredService<ITopicFactory>(),
                sp.GetRequiredService<ICacheEventFactory>(),
                sp.GetRequiredService<ICachingTelemetryProvider>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ILocalLock>(),
                sp.GetRequiredService<IDistributedLock>(),
                sp.GetRequiredService<ICachePolicyFactory>()),
            KnownCacheProviderNames.InMemory => new InMemoryCacheProvider(
                Options.Create(WithNeutralCacheKeyStrategy(sp.GetRequiredService<IOptions<InMemoryCacheOptions>>().Value)),
                sp.GetRequiredService<IOptions<CacheOptions>>(),
                sp.GetRequiredService<IMemoryCacheFactory>(),
                sp.GetRequiredService<ICacheEventFactory>(),
                sp.GetRequiredService<IChangeTokenFactory>(),
                sp.GetRequiredService<ITopicFactory>(),
                sp.GetRequiredService<ICachingTelemetryProvider>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ILocalLock>(),
                sp.GetRequiredService<ICachePolicyFactory>()),
            _ => throw new InvalidOperationException(
                $"Cache provider '{providerName}' is not supported by AddDistributedCache. " +
                $"Supported: {KnownCacheProviderNames.Redis}, {KnownCacheProviderNames.InMemoryRedis}, {KnownCacheProviderNames.InMemory}."),
        };

    /// <summary>
    /// Sole factory the multilayer tier's L2 resolves <see cref="KnownCacheProviderNames.Redis"/> against, so
    /// it reaches the distributed-keyspace provider rather than the application's.
    /// </summary>
    private static CacheFactory CreatePrivateRedisFactory(
        IServiceProvider sp, UiPathDistributedCacheOptions options, string differentiator) =>
        new CacheFactory(
            sp.GetRequiredService<IOptions<CacheOptions>>(),
            [CreateRedisProvider(sp, options, differentiator)],
            sp.GetService<ICachePolicyFactory>());

    /// <summary>
    /// The connection is resolved before the keyspace is composed, so a missing one still reports itself
    /// rather than surfacing as whatever the key strategy happens to complain about first.
    /// </summary>
    private static RedisCacheProvider CreateRedisProvider(
        IServiceProvider sp, UiPathDistributedCacheOptions options, string differentiator)
    {
        var connector = sp.GetService<IRedisConnector>() ?? throw new InvalidOperationException(
            "AddDistributedCache with a Redis-backed provider requires a Redis connection. Call AddRedisConnection on the caching builder.");
        var cacheOptions = sp.GetRequiredService<IOptions<CacheOptions>>();

        return new RedisCacheProvider(
            Options.Create(WithDistributedKeyspace(
                sp.GetRequiredService<IOptions<RedisCacheOptions>>().Value,
                options,
                cacheOptions.Value,
                differentiator)),
            cacheOptions,
            connector,
            new RedisValueSerializerProxy(sp.GetRequiredService<ISerializerProxy<byte[]>>()),
            sp.GetRequiredService<IResiliencePipelineProvider>(),
            sp.GetRequiredService<ICachingTelemetryProvider>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<ICachePolicyFactory>());
    }

    /// <summary>
    /// Copy of the Redis options carrying a distinct key differentiator, so the distributed cache's
    /// keyspace is disjoint from the application's caches. The DI singleton is left untouched.
    /// </summary>
    private static RedisCacheOptions WithDistributedKeyspace(
        RedisCacheOptions source,
        UiPathDistributedCacheOptions options,
        CacheOptions cacheOptions,
        string differentiator)
    {
        var applicationFactory = source.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory();
        var distributedFactory = options.RedisKeyStrategyFactory ?? applicationFactory;
        EnsureDifferentiatedKeyspace(
            distributedFactory, applicationFactory, cacheOptions, differentiator, ResolveCacheKeyStrategy(options));

        var copy = source.ShallowCopy();
        copy.RedisKeyStrategyFactory = new DistributedRedisKeyStrategyFactory(distributedFactory, differentiator);
        copy.CacheKeyStrategy = new DefaultCacheKeyStrategy();
        copy.AwaitRefresh = true;
        return copy;
    }

    /// <summary>
    /// Copy of the tier's options with the cache-key strategy neutralized. The multilayer caches apply
    /// <c>ICacheOptions.CacheKeyStrategy</c> (<c>HashCacheEntryBuilder</c>) on top of the key the adapter has
    /// already composed, so an application strategy configured on the tier would transform these keys a second
    /// time — making the stored key tier-dependent, and letting a lossy or ambient-insensitive strategy undo
    /// the adapter's case-sensitivity. The distributed cache composes its key through
    /// <see cref="UiPathDistributedCacheOptions.CacheKeyStrategy"/> instead. The DI singleton is left untouched.
    /// </summary>
    private static InMemoryCacheOptions WithNeutralCacheKeyStrategy(InMemoryCacheOptions source)
    {
        var copy = source.ShallowCopy();
        copy.CacheKeyStrategy = new DefaultCacheKeyStrategy();
        return copy;
    }

    /// <inheritdoc cref="WithNeutralCacheKeyStrategy(InMemoryCacheOptions)"/>
    private static InMemoryRedisCacheOptions WithNeutralCacheKeyStrategy(InMemoryRedisCacheOptions source)
    {
        var copy = source.ShallowCopy();
        copy.CacheKeyStrategy = new DefaultCacheKeyStrategy();
        return copy;
    }

    /// <summary>
    /// The differentiator only separates the keyspace if the factory honors it. A factory that ignores the
    /// value it is handed — easy to write, and inherited from the application when it configures its own —
    /// would put distributed entries under the application's own keys with nothing to show for it, so prove
    /// the composed keys actually differ instead of assuming. The two factories are kept apart because they
    /// can differ: when only the distributed one is overridden, the application still uses its own, and
    /// probing the override against itself would miss an override that lands on a real application key. The
    /// probe key goes through <paramref name="keyStrategy"/> first, so it has the shape the adapter really
    /// sends — a Redis strategy keyed on the composed shape would otherwise map live entries onto the
    /// application's keyspace while the probe sampled a name the adapter never uses.
    /// </summary>
    private static void EnsureDifferentiatedKeyspace(
        IRedisKeyStrategyFactory distributedFactory,
        IRedisKeyStrategyFactory applicationFactory,
        CacheOptions cacheOptions,
        string differentiator,
        ICacheKeyStrategy keyStrategy)
    {
        var probe = keyStrategy.GetCacheKey<byte[]>(new CacheKey("probe", CacheKeyCasing.Sensitive));
        var distributed = distributedFactory.Create(cacheOptions, differentiator).GetRedisKey(probe);

        foreach (var (description, applicationKey) in ApplicationProbes(applicationFactory, cacheOptions, probe))
        {
            if (applicationKey == distributed)
            {
                throw new InvalidOperationException(
                    $"The Redis key strategy maps distributed entries onto the same key as the application's {description} caches, so the two would share one keyspace. Either the differentiator '{differentiator}' is not distinct, or the configured IRedisKeyStrategyFactory ignores the differentiator it is given.");
            }
        }
    }

    /// <summary>
    /// The keys the application's own Redis caches would produce. <c>RedisCache</c> and <c>RedisHashCache</c>
    /// resolve their strategy through the <see cref="Type"/> overload, so the probe has to use it too: a custom
    /// factory may implement the two overloads differently, and comparing only the string one would let a
    /// colliding layout through. The string overload is still probed, for a factory that only implements that.
    /// </summary>
    private static IEnumerable<(string Description, RedisKey Key)> ApplicationProbes(
        IRedisKeyStrategyFactory inner, CacheOptions cacheOptions, CacheKey probe)
    {
        yield return ($"'{RedisTypePrefixes.String}' (ICache)", inner.Create(cacheOptions, typeof(RedisCache)).GetRedisKey(probe));
        yield return ($"'{RedisTypePrefixes.Hash}' (IHashCache)", inner.Create(cacheOptions, typeof(RedisHashCache)).GetRedisKey(probe));

        foreach (var applicationPrefix in ApplicationTypePrefixes)
        {
            yield return ($"'{applicationPrefix}'", inner.Create(cacheOptions, applicationPrefix).GetRedisKey(probe));
        }
    }

    /// <summary>
    /// Forces the differentiator regardless of what the caller asks for, so every cache built from these
    /// options lands in the distributed keyspace. The application's own factory is kept for everything else.
    /// </summary>
    private sealed class DistributedRedisKeyStrategyFactory : IRedisKeyStrategyFactory
    {
        private readonly IRedisKeyStrategyFactory _inner;
        private readonly string _differentiator;

        public DistributedRedisKeyStrategyFactory(IRedisKeyStrategyFactory inner, string differentiator)
        {
            _inner = inner;
            _differentiator = differentiator;
        }

        public IRedisKeyStrategy Create(CacheOptions options, Type cacheType) =>
            _inner.Create(options, _differentiator);

        public IRedisKeyStrategy Create(CacheOptions options, string differentiator) =>
            _inner.Create(options, _differentiator);
    }
}
