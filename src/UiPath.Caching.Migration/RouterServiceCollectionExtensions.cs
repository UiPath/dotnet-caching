using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Broadcast.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration;

public static class RouterServiceCollectionExtensions
{
    internal const string StackName = "UiPath.Caching.Migration";

    /// <summary>
    /// Routes the registered caches between the application source and a Redis-only library stack on the application own connector.
    /// <c>AddCaching</c> with <c>AddRedisConnection</c> must be registered too.
    /// </summary>
    public static IServiceCollection AddCachingRouter(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<RouterBuilder> configure,
        string sectionName = "Caching")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new RouterBuilder();
        configure(builder);
        var predicate = builder.LibraryPredicate ?? throw new InvalidOperationException("The router needs UseLibraryWhen: the predicate that picks the path of a group.");
        var envelope = builder.Envelope ?? throw new InvalidOperationException("The router needs UseEnvelope: the serializer of the older client's value format.");
        if (builder.Registrations.Count == 0)
        {
            throw new InvalidOperationException("The router has no registered cache.");
        }

        var processId = builder.NodeIdentity ?? $"{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";
        var registered = services.Count;
        services
            .AddNamedCachingOnPrimaryConnection(
                StackName,
                configuration,
                chain =>
                {
                    chain.AddRedis(ConfigureStackRedis);
                    chain.AddBroadcast();
                    chain.Services.TryAddSingleton<IEventFormatterProxy<ICacheEvent>, CacheEventFormatter>();
                    chain.Services.TryAddSingleton<ICacheEventFactory, CacheEventFactory>();
                },
                sectionName,
                (stack, _) =>
                {
                    stack.AddSingleton<ISerializerProxy<byte[]>>(envelope);

                    // After the stack's own `?stack=` post-configure, so the process id lands in the host part of the identity.
                    stack.PostConfigure<CacheOptions>(options => options.SourceUri = WithProcess(options.SourceUri ?? CacheOptions.MachineUri, processId));
                })
            .Expose<ICacheEventFactory>()
            .Expose<IOptions<CacheOptions>>()
            .Expose<IOptions<RedisCacheOptions>>()
            .Expose(stack => new StackServices([.. stack.GetServices<IHostedService>()]));

        // The router builds and starts its stack itself, behind its guarded start; a hosted service of the host would build it first and fail the host.
        for (var index = services.Count - 1; index >= registered; index--)
        {
            if (services[index].ServiceType == typeof(IHostedService))
            {
                services.RemoveAt(index);
            }
        }

        var section = configuration.GetSection(sectionName);
        services.AddSingleton<IRouter>(root => CreateRouter(root, builder, predicate, processId, section));
        services.AddHostedService<RouterHostedService>();
        return services;
    }

    internal static Func<TimeSpan> LibrarySettleWithin(IServiceProvider root, TimeSpan? explicitBound)
    {
        if (explicitBound is { } bound)
        {
            return () => bound;
        }

        var options = root.GetRequiredKeyedService<IOptions<RedisCacheOptions>>(StackName).Value;
        var connector = root.GetRequiredService<IRedisConnector>();
        ThrowIfSettleBeyondHorizon(root);

        // A primary is listed only for a finished connection, so this never waits for one; once seen, the configuration is kept.
        return new LibrarySettleSnapshot(() => options.Timeout, () => connector.GetPrimaries().FirstOrDefault()?.Multiplexer.Configuration).Current;
    }

    internal static void ThrowIfSettleBeyondHorizon(IServiceProvider root)
    {
        var connection = root.GetService<IOptions<RedisConnectionOptions>>()?.Value;
        LibrarySettleBound.ThrowIfBeyondHorizon(LibrarySettleBound.Join(connection?.ConnectionString, connection?.ConnectionStringExtraParams));
    }

    /// <summary>The key the stack's node list store uses: the same name, with braces escaped, through the stack's key strategy.</summary>
    internal static RedisKey NodeListKey(CacheOptions cacheOptions, RedisCacheOptions redisOptions)
    {
        var name = string.Join(cacheOptions.Separator, "Caching", "Nodes")
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("{", "%7B", StringComparison.Ordinal)
            .Replace("}", "%7D", StringComparison.Ordinal);
        return (redisOptions.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory()).Create(cacheOptions, RedisKeyspaces.String).GetRedisKey(name);
    }

    /// <summary>The identity the router's adverts and bridge messages carry: the stack's source identity, from the router's section as the stack binds it.</summary>
    internal static string RouterIdentity(IConfigurationSection section, string processId)
    {
        var options = new CacheOptions();
        section.Bind(options);
        return StackIdentity(options.SourceUri ?? CacheOptions.MachineUri, processId).ToString().ToLowerInvariant();
    }

    /// <summary>The router stack's source identity: the named stack's <c>?stack=</c> form, then the process part.</summary>
    internal static Uri StackIdentity(Uri source, string processId) =>
        WithProcess(new Uri($"{source.AbsoluteUri.Split('?')[0]}?stack={Uri.EscapeDataString(StackName)}"), processId);

    internal static Uri WithProcess(Uri source, string processId)
    {
        var text = source.AbsoluteUri;
        var query = text.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? new Uri($"{text}-p{processId}") : new Uri($"{text[..query]}-p{processId}{text[query..]}");
    }

    /// <summary>The stack's Redis options: set here only, never bound from configuration, so the validator can check them without building the stack.</summary>
    internal static void ConfigureStackRedis(RedisCacheOptions options)
    {
        options.RedisKeyStrategyFactory = PassThroughKeyStrategyFactory.Instance;
        options.RemoveUnreadable = false;

        // A stored null then reads as found; each registration decides with CacheNulls whether that is a hit. The router never writes a null.
        options.CacheNullValues = true;
    }

    private static Router CreateRouter(IServiceProvider root, RouterBuilder builder, Func<string, bool> predicate, string processId, IConfigurationSection section)
    {
        var logger = root.GetService<ILoggerFactory>()?.CreateLogger("UiPath.Caching.Migration") ?? NullLogger.Instance;
        var core = new RouterCore(root.GetRequiredService<TimeProvider>(), predicate, logger)
        {
            BypassFlag = builder.BypassFlag,
            SourceTimeout = builder.SourceTimeout ?? TimeSpan.FromSeconds(30),
            CleanupBound = builder.CleanupBound,
        };
        try
        {
            // The one check that fails the host: a setting no library start could ever satisfy.
            if (builder.SettleBound is null)
            {
                ThrowIfSettleBeyondHorizon(root);
            }

            // The other check that fails the host: settings that would make the library write what the source cannot read.
            // Bound as the named stack binds them, without building it: a stack that cannot be built must not fail the host here.
            var stackCache = new CacheOptions();
            section.Bind(stackCache);
            var stackRedis = new RedisCacheOptions();
            ConfigureStackRedis(stackRedis);
            RegistrationValidator.ThrowIfInvalid(stackCache, stackRedis);
            logger.LogInformation("Caching router library options: {Options}", RegistrationValidator.Describe(stackCache, stackRedis));

            // Everything that builds, starts or reaches the library is behind the factory: a throw or a hang there leaves the router on the source.
            core.UseLibrary(token => StartLibraryAsync(root, builder, token));
            if (builder.Bridge is { } bridge)
            {
                core.Bridge = new BridgeHost(bridge, RouterIdentity(section, processId), core, logger) { MaxPending = builder.BridgeMaxPending };
            }

            var advertiser = CreateAdvertiser(root, builder, core, processId, section, logger);
            var router = new Router(core, builder.Registrations, builder.Excluded) { Advertiser = advertiser, Envelope = builder.Envelope };

            RouterBanner.Log(logger, builder, processId);
            router.Start();
            return router;
        }
        catch
        {
            core.CancelBackground();
            core.DisposeResources();
            throw;
        }
    }

    /// <summary>From the router's section and the process id, never from the library's container, so a library build that hangs or throws cannot hold or disable it.</summary>
    private static RouterAdvertiser? CreateAdvertiser(IServiceProvider root, RouterBuilder builder, RouterCore core, string processId, IConfigurationSection section, ILogger logger)
    {
        try
        {
            var cacheOptions = new CacheOptions();
            section.Bind(cacheOptions);
            // The one timing source: the router's own section, which the stack does not bind for itself (its Redis options are set in code).
            var negotiation = new FormatNegotiationOptions();
            section.GetSection("Redis:FormatNegotiation").Bind(negotiation);
            if (negotiation.AdvertiseInterval <= TimeSpan.Zero || negotiation.LivenessWindow <= TimeSpan.Zero)
            {
                logger.LogWarning("The router runs without a node advertisement: Redis:FormatNegotiation AdvertiseInterval and LivenessWindow must be positive.");
                return null;
            }

            Func<CancellationToken, ValueTask<IDatabase>> database = builder.SourceDatabase is { } source
                ? _ => ValueTask.FromResult(source())
                : async token =>
                {
                    var connector = root.GetRequiredService<IRedisConnector>();
                    await connector.ConnectAsync(token).ConfigureAwait(false);
                    return connector.Database;
                };
            var build = typeof(Router).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
            return new RouterAdvertiser(
                new RouterAdvertisementStore(database, NodeListKey(new CacheOptions { Separator = cacheOptions.Separator }, new RedisCacheOptions { RedisKeyStrategyFactory = PassThroughKeyStrategyFactory.Instance })),
                RouterIdentity(section, processId),
                build,
                [new BypassTokens(core)],
                negotiation.AdvertiseInterval,
                negotiation.LivenessWindow,
                core.Clock,
                logger);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "The router could not set up its node advertisement; it runs without one.");
            return null;
        }
    }

    private static async Task<LibraryParts> StartLibraryAsync(IServiceProvider root, RouterBuilder builder, CancellationToken cancellationToken)
    {
        var started = new List<IHostedService>();
        try
        {
            foreach (var service in root.GetRequiredKeyedService<StackServices>(StackName).HostedServices)
            {
                await service.StartAsync(cancellationToken).ConfigureAwait(false);
                started.Add(service);
            }

            var options = root.GetRequiredKeyedService<IOptions<CacheOptions>>(StackName).Value;
            var source = options.SourceUri ?? CacheOptions.MachineUri;
            var topics = root.GetRequiredKeyedService<ITopicFactory>(StackName).Get();
            var connector = root.GetRequiredService<IRedisConnector>();
            var streams = new PrefixStrategy(RedisKeyspaces.Streams, options);
            var group = source.ToString().ToLowerInvariant();
            return new LibraryParts(
                root.GetRequiredKeyedService<ICacheFactory>(StackName).CreateCache(KnownCacheProviderNames.Redis).AsMigrationCache(),
                connector,
                topics,
                root.GetRequiredKeyedService<ICacheEventFactory>(StackName),
                source,
                LibrarySettleWithin(root, builder.SettleBound),
                started,
                key => ReleaseTopicAsync(topics, connector, streams.GetRedisKey(key), group, key));
        }
        catch
        {
            foreach (var service in Enumerable.Reverse(started))
            {
                try
                {
                    await BoundedStop.CompletesAsync(service.StopAsync(CancellationToken.None), builder.CleanupBound).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The start failure is the one worth reporting.
                    _ = ex;
                }
            }

            throw;
        }
    }

    /// <summary>The topic is removed first, so its reader is gone before the group is, and cannot make it again.</summary>
    private static async Task ReleaseTopicAsync(ITopicProvider topics, IRedisConnector connector, RedisKey stream, string group, TopicKey key)
    {
        topics.Remove(key);
        try
        {
            await connector.Database.ExecuteAsync("XGROUP", "DESTROY", stream, group).ConfigureAwait(false);
        }
        catch (RedisServerException)
        {
            // No such stream or group: nothing of ours is left.
        }
    }
}
