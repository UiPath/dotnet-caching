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
using UiPath.Caching.PhaseB;
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
        var gate = builder.PinPredicate is null ? null : new PinFormatGate();
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
                    if (gate is not null)
                    {
                        stack.AddSingleton<IFormatWriteGate>(gate);
                    }

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
        services.AddSingleton<IRouter>(root => CreateRouter(root, builder, predicate, processId, gate, section));
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

    /// <summary>The router stack's source identity: the named stack's <c>?stack=</c> form, then the process part.</summary>
    internal static Uri StackIdentity(Uri source, string processId) =>
        WithProcess(new Uri($"{source.AbsoluteUri.Split('?')[0]}?stack={Uri.EscapeDataString(StackName)}"), processId);

    internal static Uri WithProcess(Uri source, string processId)
    {
        var text = source.AbsoluteUri;
        var query = text.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? new Uri($"{text}-p{processId}") : new Uri($"{text[..query]}-p{processId}{text[query..]}");
    }

    private static Router CreateRouter(IServiceProvider root, RouterBuilder builder, Func<string, bool> predicate, string processId, PinFormatGate? gate, IConfigurationSection section)
    {
        var logger = root.GetService<ILoggerFactory>()?.CreateLogger("UiPath.Caching.Migration") ?? NullLogger.Instance;
        var core = new RouterCore(root.GetRequiredService<TimeProvider>(), predicate, logger)
        {
            BypassFlag = builder.BypassFlag,
            SourceTimeout = builder.SourceTimeout ?? TimeSpan.FromSeconds(30),
            CleanupBound = builder.CleanupBound,
        };
        AdvertisementSetup? setup = null;
        var phaseB = builder.PhaseB is { } phaseSetup ? new PhaseBNode(phaseSetup, root.GetRequiredService<TimeProvider>()) : null;
        try
        {
            // The one check that fails the host: a setting no library start could ever satisfy.
            if (builder.SettleBound is null)
            {
                ThrowIfSettleBeyondHorizon(root);
            }

            // Everything that builds, starts or reaches the library is behind the factory: a throw or a hang there leaves the router on the source.
            core.UseLibrary(token => StartLibraryAsync(root, builder, phaseB, setup, token));
            setup = AdvertisementSetup.TryCreate(root, builder, processId, section, logger);
            if (builder.PinPredicate is { } pinWhen)
            {
                ValidatePinSetup(section, setup, logger);

                // Before the caches, which take their group's pin from it.
                var pins = new PinCoordinator(core, pinWhen, setup?.PinStore, setup?.Scope, setup?.RefreshInterval ?? TimeSpan.FromSeconds(15), setup?.MaxViewAge ?? TimeSpan.FromMinutes(2), logger);
                core.Pins = pins;
                gate!.Attach(pins);
            }

            var advertiser = setup is null ? null : CreateAdvertiser(setup, core, phaseB, logger);
            if (advertiser is not null && core.Pins is { } attached)
            {
                attached.OwnField = advertiser.Field;
                attached.Fresh = advertiser.Fresh;
            }

            var router = new Router(core, builder.Registrations) { Advertiser = advertiser, PhaseB = phaseB };

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

    private static void ValidatePinSetup(IConfigurationSection section, AdvertisementSetup? setup, ILogger logger)
    {
        var negotiation = new FormatNegotiationOptions();
        section.GetSection("Redis:FormatNegotiation").Bind(negotiation);
        if (negotiation.Mode == FormatNegotiationMode.Force)
        {
            throw new InvalidOperationException("UsePinWhen cannot be combined with Redis:FormatNegotiation:Mode Force: Force writes the format without asking the pin gate. Use Auto.");
        }

        if (setup is null)
        {
            throw new InvalidOperationException("UsePinWhen needs the router's pin store, and the node advertisement it lives in could not be set up (see the warning logged before this), so no group could ever be known unpinned.");
        }

        var needed = negotiation.LivenessWindow + (negotiation.AdvertiseInterval * 2);
        if (negotiation.UpgradeHold < needed)
        {
            throw new InvalidOperationException($"UsePinWhen needs Redis:FormatNegotiation:UpgradeHold of at least LivenessWindow + 2 x AdvertiseInterval ({needed}), so a node that joins while the switch is recorded is seen before a format is written; it is {negotiation.UpgradeHold}.");
        }

        if (setup.Scope is null)
        {
            logger.LogWarning("UsePinWhen is set but the application has no AppShortName, so the pin gate cannot attribute nodes to this application and stays closed.");
        }
    }
    private static RouterAdvertiser CreateAdvertiser(AdvertisementSetup setup, RouterCore core, PhaseBNode? phaseB, ILogger logger)
    {
        var sources = new List<IAdvertisementTokens> { new BypassTokens(core), new ScopeTokens(setup.Scope) };
        if (core.Pins is { } pins)
        {
            sources.Add(pins);
        }

        if (phaseB is { HasGroups: true })
        {
            sources.Add(new PhaseBTokens(phaseB.Tokens));
        }

        return new RouterAdvertiser(setup.Store, setup.NodeId, setup.Build, sources, setup.RefreshInterval, setup.LivenessWindow, core.Clock, logger);
    }

    private static async Task<LibraryParts> StartLibraryAsync(IServiceProvider root, RouterBuilder builder, PhaseBNode? phaseB, AdvertisementSetup? setup, CancellationToken cancellationToken)
    {
        var started = new List<IHostedService>();
        try
        {
            foreach (var service in root.GetRequiredKeyedService<StackServices>(StackName).HostedServices)
            {
                await service.StartAsync(cancellationToken).ConfigureAwait(false);
                started.Add(service);
            }

            var connector = root.GetRequiredService<IRedisConnector>();
            if (phaseB is { HasGroups: true })
            {
                // The leader needs the node entries of the router; without the advertisement it still acknowledges and polls, and leads nothing.
                var prefix = root.GetService<IOptions<RedisCacheOptions>>()?.Value.KeyPrefix ?? string.Empty;
                var hosted = new PhaseBHostedService(phaseB, connector, prefix, setup is null ? null : new RouterAdvertSource(setup.Store), setup?.Scope);
                await hosted.StartAsync(cancellationToken).ConfigureAwait(false);
                started.Add(hosted);
            }

            var options = root.GetRequiredKeyedService<IOptions<CacheOptions>>(StackName).Value;
            var source = options.SourceUri ?? CacheOptions.MachineUri;
            var topics = root.GetRequiredKeyedService<ITopicFactory>(StackName).Get();
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
