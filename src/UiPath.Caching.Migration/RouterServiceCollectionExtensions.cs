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
using UiPath.Caching.KeyMove;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration;

public static class RouterServiceCollectionExtensions
{
    internal const string StackName = "UiPath.Caching.Migration";

    /// <summary>Routes the registered caches between the application source and a Redis-only library stack on the application own connector. <c>AddCaching</c> with <c>AddRedisConnection</c> must be registered too.</summary>
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
        if (builder.Registrations.Count == 0)
        {
            throw new InvalidOperationException("The router has no registered cache.");
        }

        if (builder.Bridge is not null && builder.Envelope is null)
        {
            throw new InvalidOperationException("UseChannelBridge needs UseEnvelope: the bridge carries the older client's invalidations, and without an envelope the in-place keys hold the library's own format.");
        }

        if (builder.WriteFormatPredicate is not null && builder.PinPredicate is null)
        {
            throw new InvalidOperationException("UseWriteFormatWhen needs UsePinWhen: a group writes MessagePack only while it is pinned and switched.");
        }

        if (builder.WriteFormatPredicate is not null && builder.ValueSerializers is null)
        {
            throw new InvalidOperationException("UseWriteFormatWhen needs UseValueSerializers: the router's stack must register the format its groups write.");
        }

        builder.BuildKeyMove();
        if (builder.KeyMove is { Groups.Count: > 0 })
        {
            // The mover scans the application's connection, the one the key move node runs on.
            services.TryAddSingleton<IScanUnits>(root => new MultiplexerScanUnits(() => root.GetRequiredService<IRedisConnector>().Database.Multiplexer));
            services.AddMoverRate();
        }

        var processId = builder.NodeIdentity ?? $"{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";
        var gate = builder.PinPredicate is null ? null : new PinFormatGate();
        RegisterLibraryStack(services, configuration, sectionName, builder, processId, gate);

        var section = configuration.GetSection(sectionName);
        services.AddSingleton<IRouter>(root => CreateRouter(root, builder, predicate, processId, gate, section));
        services.AddHostedService<RouterHostedService>();
        return services;
    }

    /// <summary>The envelope in the stack's container, marked header-free so the stack reads it beside a registered format.</summary>
    internal static void AddEnvelope(IServiceCollection stack, ISerializerProxy<byte[]> envelope) => stack.AddSingleton(HeaderFreeEnvelope.Wrap(envelope));

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

    /// <summary>Both sides of a key move need a cache that can encode: the application's for the native format and the router's own for the in-place one.</summary>
    internal static void ValidateCodecCaches(ICacheFactory application, ICacheFactory router)
    {
        _ = CodecCacheOf(application) ?? throw MissingNativeCodecCache();
        _ = CodecCacheOf(router) ?? throw MissingRouterCodecCache();
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

        // Each registration opts into treating a cached null as a hit.
        options.CacheNullValues = true;
    }

    /// <summary>The topic is removed first, so its reader is gone before the group is, and cannot make it again.</summary>
    /// <remarks>The connection is awaited, never read synchronously, so a Redis that cannot be reached leaves a task the caller's bound covers.</remarks>
    internal static async Task ReleaseTopicAsync(ITopicProvider topics, IRedisConnector connector, RedisKey stream, string group, TopicKey key, CancellationToken cancellationToken)
    {
        topics.Remove(key);
        try
        {
            await connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await connector.Database.ExecuteAsync("XGROUP", "DESTROY", stream, group).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RedisServerException)
        {
            // No such stream or group: nothing of ours is left.
        }
    }

    private static void RegisterLibraryStack(IServiceCollection services, IConfiguration configuration, string sectionName, RouterBuilder builder, string processId, PinFormatGate? gate)
    {
        var registered = services.Count;
        services
            .AddNamedCachingOnPrimaryConnection(
                StackName,
                configuration,
                chain =>
                {
                    chain.AddRedis(options =>
                    {
                        ConfigureStackRedis(options);

                        // With a write format setting, the stack's negotiation takes the timings the pin was validated against, so the hold the pin checks is the hold the stack applies; a router without one keeps the stack's own defaults.
                        if (builder.WriteFormatPredicate is not null)
                        {
                            configuration.GetSection(sectionName).GetSection("Redis:FormatNegotiation").Bind(options.FormatNegotiation);
                        }
                    });
                    builder.ValueSerializers?.Invoke(chain);
                    chain.AddBroadcast();
                    chain.Services.TryAddSingleton<IEventFormatterProxy<ICacheEvent>, CacheEventFormatter>();
                    chain.Services.TryAddSingleton<ICacheEventFactory, CacheEventFactory>();
                },
                sectionName,
                (stack, _) =>
                {
                    if (builder.Envelope is { } envelope)
                    {
                        AddEnvelope(stack, envelope);
                    }

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
            .Expose<ISerializerProxy<byte[]>>()
            .Expose(stack => new StackServices([.. stack.GetServices<IHostedService>()]));

        RemoveStackHostedServices(services, registered);
    }

    private static void RemoveStackHostedServices(IServiceCollection services, int registered)
    {
        // The router builds and starts its stack itself, behind its guarded start; a hosted service of the host would build it first and fail the host.
        for (var index = services.Count - 1; index >= registered; index--)
        {
            if (services[index].ServiceType == typeof(IHostedService))
            {
                services.RemoveAt(index);
            }
        }
    }

    private static Router CreateRouter(IServiceProvider root, RouterBuilder builder, Func<string, bool> predicate, string processId, PinFormatGate? gate, IConfigurationSection section)
    {
        // A deterministic configuration error: it fails the host here, once, and never leaves a move cache on the source path.
        if (builder.KeyMove is { Caches.Count: > 0 })
        {
            ValidateCodecCaches(root.GetRequiredService<ICacheFactory>(), root.GetRequiredKeyedService<ICacheFactory>(StackName));
        }

        var logger = root.GetService<ILoggerFactory>()?.CreateLogger("UiPath.Caching.Migration") ?? NullLogger.Instance;
        var core = new RouterCore(root.GetRequiredService<TimeProvider>(), predicate, logger)
        {
            BypassFlag = builder.BypassFlag,
            SourceTimeout = builder.SourceTimeout ?? TimeSpan.FromSeconds(30),
            CleanupBound = builder.CleanupBound,
        };
        AdvertisementSetup? setup = null;

        var keyMove = builder.KeyMove is { } phaseSetup ? new KeyMoveNode(phaseSetup, root.GetRequiredService<TimeProvider>()) : null;
        try
        {
            ValidateLibraryOptions(root, builder, section, logger);

            // Everything that builds, starts or reaches the library is behind the factory: a throw or a hang there leaves the router on the source.
            core.UseLibrary(token => StartLibraryAsync(root, builder, keyMove, setup, token));
            if (builder.Bridge is { } bridge)
            {
                core.Bridge = new BridgeHost(bridge, RouterIdentity(section, processId), core, logger) { MaxPending = builder.BridgeMaxPending };
            }

            setup = AdvertisementSetup.TryCreate(root, builder, processId, section, logger);
            ConfigurePin(builder, core, gate, setup, section, keyMove, logger);

            var advertiser = setup is null ? null : CreateAdvertiser(setup, core, keyMove, logger);
            if (advertiser is not null && core.Pins is { } attached)
            {
                attached.OwnField = advertiser.Field;
                attached.Fresh = advertiser.Fresh;
            }

            var router = new Router(core, builder.Registrations, builder.Excluded) { Advertiser = advertiser, Envelope = builder.Envelope, KeyMove = keyMove };

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

    private static void ValidateLibraryOptions(IServiceProvider root, RouterBuilder builder, IConfigurationSection section, ILogger logger)
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
    }

    private static void ConfigurePin(RouterBuilder builder, RouterCore core, PinFormatGate? gate, AdvertisementSetup? setup, IConfigurationSection section, KeyMoveNode? keyMove, ILogger logger)
    {
        if (builder.PinPredicate is { } pinWhen)
        {
            ValidatePinSetup(section, setup, logger);

            // Before the caches, which take their group's pin from it.
            var pins = new PinCoordinator(core, pinWhen, setup?.PinStore, setup?.Scope, setup?.RefreshInterval ?? TimeSpan.FromSeconds(15), setup?.MaxViewAge ?? TimeSpan.FromMinutes(2), logger) { WriteFormat = builder.WriteFormatPredicate };
            core.Pins = pins;
            if (builder.WriteFormatPredicate is { } writeFormat)
            {
                core.WriteFormats = new WriteFormatSelector(writeFormat, pins, logger)
                {
                    MoveHold = keyMove is { HasGroups: true } && builder.KeyMove is { } moves ? WriteFormatSelector.HoldFrom(keyMove.Monitor, moves.Groups.Select(g => g.Name).ToHashSet(StringComparer.Ordinal)) : null,
                    Alert = builder.KeyMove?.Alert,
                };
            }
            gate!.Attach(pins);
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
    private static RouterAdvertiser CreateAdvertiser(AdvertisementSetup setup, RouterCore core, KeyMoveNode? keyMove, ILogger logger)
    {
        var sources = new List<IAdvertisementTokens> { new BypassTokens(core), new ScopeTokens(setup.Scope) };
        if (core.Pins is { } pins)
        {
            sources.Add(pins);
        }

        if (keyMove is { HasGroups: true })
        {
            sources.Add(new KeyMoveTokens(keyMove.Tokens));
        }

        return new RouterAdvertiser(setup.Store, setup.NodeId, setup.Build, sources, setup.RefreshInterval, setup.LivenessWindow, core.Clock, logger);
    }

    private static List<MoverHost> MoverHosts(IServiceProvider root, KeyMoveSetup setup, KeyMoveNode node, IKeyMovePlane plane, MoveTargets movers)
    {
        var prefix = root.GetService<IOptions<RedisCacheOptions>>()?.Value.KeyPrefix ?? string.Empty;
        var units = root.GetRequiredService<IScanUnits>();
        var rate = root.GetRequiredService<IMoveRate>();
        var time = root.GetRequiredService<TimeProvider>();
        return [.. setup.Groups.Select(g => new MoverHost(g.Name, plane, setup.KeySpace, movers.Of(g.Name), units, ct => node.ReadStateAsync(g.Name, ct), prefix, time, rate))];
    }

    private static LibraryParts CollectLibraryParts(IServiceProvider root, RouterBuilder builder, IRedisConnector connector, List<IHostedService> started, IKeyMovePlane? plane, MoveTargets? movers)
    {
        var options = root.GetRequiredKeyedService<IOptions<CacheOptions>>(StackName).Value;
        var source = options.SourceUri ?? CacheOptions.MachineUri;
        var topics = root.GetRequiredKeyedService<ITopicFactory>(StackName).Get();
        var streams = new PrefixStrategy(RedisKeyspaces.Streams, options);
        var group = source.ToString().ToLowerInvariant();
        var libraryCache = root.GetRequiredKeyedService<ICacheFactory>(StackName).CreateCache(KnownCacheProviderNames.Redis);
        var binder = builder.KeyMove is { Caches.Count: > 0 } registered && plane is not null ? KeyMoveBinderFor(root, libraryCache, registered, builder.InPlaceKeepsSystemFields) : null;
        return new LibraryParts(
            libraryCache.AsMigrationCache(),
            connector,
            topics,
            root.GetRequiredKeyedService<ICacheEventFactory>(StackName),
            source,
            LibrarySettleWithin(root, builder.SettleBound),
            started,
            (key, cancellationToken) => ReleaseTopicAsync(topics, connector, streams.GetRedisKey(key), group, key, cancellationToken),
            plane,
            libraryCache,
            root.GetRequiredKeyedService<IOptions<RedisCacheOptions>>(StackName).Value.CacheNullValues,
            binder,
            movers);
    }

    // The in-place side is the router's own library cache, so the codec cannot differ from what the library path writes, with or without an envelope; the native side is the application's Redis cache, with its serializers and effective format.
    // The hash codec caches are made the same way and are optional: a host that registers no hash cache in a key move group never asks for them, and binding a hash cache without them fails at startup.
    private static KeyMoveBinder KeyMoveBinderFor(IServiceProvider root, ICache routerCache, KeyMoveSetup setup, bool inPlaceKeepsSystemFields)
    {
        if (routerCache is not IByteCodecCache inPlace)
        {
            throw MissingRouterCodecCache();
        }

        // Only a group that holds hash caches asks for the hash codec caches.
        var hashes = setup.Caches.Any(c => c.Kind == KeyMoveKind.Hash);
        return new KeyMoveBinder(
            inPlace,
            NativeCodecCache(root) ?? throw MissingNativeCodecCache(),
            setup.Caches,
            classes: setup.Values as KeyMoveValueClasses,
            inPlaceHash: hashes ? HashCodecCacheOf(root.GetRequiredKeyedService<ICacheFactory>(StackName)) : null,
            nativeHash: hashes ? HashCodecCacheOf(root.GetRequiredService<ICacheFactory>()) : null,
            inPlaceKeepsSystemFields: inPlaceKeepsSystemFields);
    }

    private static IByteHashCodecCache? HashCodecCacheOf(ICacheFactory factory) =>
        factory.CreateHashCache(KnownCacheProviderNames.Redis) as IByteHashCodecCache;

    // CreateCache falls back to the default cache, or a null cache, when there is no enabled Redis provider, so what comes back is checked by what it can do.
    private static IByteCodecCache? CodecCacheOf(ICacheFactory factory) =>
        factory.CreateCache(KnownCacheProviderNames.Redis) as IByteCodecCache;

    private static IByteCodecCache? NativeCodecCache(IServiceProvider root) => CodecCacheOf(root.GetRequiredService<ICacheFactory>());

    private static InvalidOperationException MissingNativeCodecCache() =>
        new("UseKeyMove needs the application's Redis cache for the native format: add AddRedis to the application's caching and enable the Redis cache provider.");

    private static InvalidOperationException MissingRouterCodecCache() =>
        new("UseKeyMove needs the router's own Redis cache for the in-place format: enable the Redis cache provider in the router's caching section.");

    private static async Task<LibraryParts> StartLibraryAsync(IServiceProvider root, RouterBuilder builder, KeyMoveNode? keyMove, AdvertisementSetup? setup, CancellationToken cancellationToken)
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
            if (keyMove is { HasGroups: true })
            {
                // The leader needs the node entries of the router; without the advertisement it still acknowledges and polls, and leads nothing.
                var prefix = root.GetService<IOptions<RedisCacheOptions>>()?.Value.KeyPrefix ?? string.Empty;
                var hosted = new KeyMoveHostedService(keyMove, connector, prefix, setup is null ? null : new RouterAdvertSource(setup.Store), setup?.Scope);
                await hosted.StartAsync(cancellationToken).ConfigureAwait(false);
                started.Add(hosted);
            }

            IKeyMovePlane? plane = null;
            MoveTargets? movers = null;
            if (keyMove is { HasGroups: true } && builder.KeyMove is { } moveSetup)
            {
                plane = new NodePlane(keyMove, root.GetRequiredService<TimeProvider>(), moveSetup.Alert);
                movers = new MoveTargets();

                // After the node, so the router stops the movers first.
                var mover = new MoverHostedService(MoverHosts(root, moveSetup, keyMove, plane, movers));
                await mover.StartAsync(cancellationToken).ConfigureAwait(false);
                started.Add(mover);
            }

            return CollectLibraryParts(root, builder, connector, started, plane, movers);
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
}
