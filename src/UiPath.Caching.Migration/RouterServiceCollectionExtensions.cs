using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UiPath.Caching.Broadcast;
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
                (stack, _) => stack.AddSingleton<ISerializerProxy<byte[]>>(envelope))
            .Expose<ICacheEventFactory>()
            .Expose<IOptions<CacheOptions>>()
            .Expose<IOptions<RedisCacheOptions>>();
        services.AddSingleton<IRouter>(root => CreateRouter(root, builder, predicate));
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
        var connection = root.GetService<IOptions<RedisConnectionOptions>>()?.Value;
        LibrarySettleBound.ThrowIfBeyondHorizon(LibrarySettleBound.Join(connection?.ConnectionString, connection?.ConnectionStringExtraParams));

        // A primary is listed only for a finished connection, so this never waits for one; once seen, the configuration is kept.
        return new LibrarySettleSnapshot(() => options.Timeout, () => connector.GetPrimaries().FirstOrDefault()?.Multiplexer.Configuration).Current;
    }
    private static Router CreateRouter(IServiceProvider root, RouterBuilder builder, Func<string, bool> predicate)
    {
        var core = new RouterCore(
            root.GetRequiredService<TimeProvider>(),
            predicate,
            root.GetService<ILoggerFactory>()?.CreateLogger("UiPath.Caching.Migration") ?? NullLogger.Instance);
        try
        {
            var cache = root.GetRequiredKeyedService<ICacheFactory>(StackName).CreateCache(KnownCacheProviderNames.Redis);
            var context = new RouterContext(
                core,
                cache.AsMigrationCache(),
                root.GetRequiredService<IRedisConnector>(),
                root.GetRequiredKeyedService<ITopicFactory>(StackName).Get(),
                root.GetRequiredKeyedService<ICacheEventFactory>(StackName),
                root.GetRequiredKeyedService<IOptions<CacheOptions>>(StackName).Value.SourceUri ?? CacheOptions.MachineUri,
                LibrarySettleWithin(root, builder.SettleBound));
            return new Router(core, builder.Registrations, context);
        }
        catch
        {
            core.CancelBackground();
            core.DisposeResources();
            throw;
        }
    }
}
