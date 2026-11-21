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
                    chain.AddRedis(options =>
                    {
                        options.RedisKeyStrategyFactory = PassThroughKeyStrategyFactory.Instance;
                        options.RemoveUnreadable = false;
                    });
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

        services.AddSingleton<IRouter>(root => CreateRouter(root, builder, predicate, processId));
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

    internal static Uri WithProcess(Uri source, string processId)
    {
        var text = source.AbsoluteUri;
        var query = text.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? new Uri($"{text}-p{processId}") : new Uri($"{text[..query]}-p{processId}{text[query..]}");
    }

    private static Router CreateRouter(IServiceProvider root, RouterBuilder builder, Func<string, bool> predicate, string processId)
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

            // Everything that builds, starts or reaches the library is behind the factory: a throw or a hang there leaves the router on the source.
            core.UseLibrary(token => StartLibraryAsync(root, builder, token));
            var advertiser = CreateAdvertiser(root, builder, core, logger);
            var router = new Router(core, builder.Registrations) { Advertiser = advertiser };

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

    /// <summary>Reads only the stack's options, so it needs no started library; a failure leaves the router without an advertisement, never the host down.</summary>
    private static RouterAdvertiser? CreateAdvertiser(IServiceProvider root, RouterBuilder builder, RouterCore core, ILogger logger)
    {
        try
        {
            var cacheOptions = root.GetRequiredKeyedService<IOptions<CacheOptions>>(StackName).Value;
            var redisOptions = root.GetRequiredKeyedService<IOptions<RedisCacheOptions>>(StackName).Value;
            var database = builder.SourceDatabase ?? (() => root.GetRequiredService<IRedisConnector>().Database);
            var build = typeof(Router).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
            return new RouterAdvertiser(
                new RouterAdvertisementStore(database, NodeListKey(cacheOptions, redisOptions)),
                (cacheOptions.SourceUri ?? CacheOptions.MachineUri).ToString().ToLowerInvariant(),
                build,
                [new BypassTokens(core)],
                redisOptions.FormatNegotiation.AdvertiseInterval,
                redisOptions.FormatNegotiation.LivenessWindow,
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
