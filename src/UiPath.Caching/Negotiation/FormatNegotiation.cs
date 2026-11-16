using System.Reflection;
using UiPath.Caching.Broadcast.Redis;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Negotiation;

/// <summary>Everything one stack needs to advertise what it reads and, in <see cref="FormatNegotiationMode.Auto"/>, to decide what it writes; <see cref="Inactive"/> for an app with no serializer registered: then nothing is written, read or scheduled.</summary>
internal sealed class FormatNegotiation
{
    public static FormatNegotiation Inactive { get; } = new();

    /// <summary>Null in <see cref="FormatNegotiationMode.Force"/>: the node advertises, and writes exactly what its policies say.</summary>
    public FormatNegotiator? Negotiator { get; init; }

    public NodeAdvertisement? Advertisement { get; init; }

    public string NodeId { get; init; } = string.Empty;

    public NodeListStore Store { get; init; } = default!;

    public StreamNodeList NodeList { get; init; } = default!;

    /// <summary>The streams this node's topics read; <see langword="null"/> when the app has no Streams transport, so the node list is blind.</summary>
    public Func<IReadOnlyList<RedisKey>>? StreamKeys { get; init; }

    public FormatNegotiationOptions Options { get; init; } = new();

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public ILogger Logger { get; init; } = NullLogger.Instance;

    public bool IsActive => Advertisement is not null;

    public static FormatNegotiation Create(IServiceProvider services)
    {
        var redisOptions = services.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
        var options = redisOptions.FormatNegotiation;
        var registry = services.GetRequiredService<ValueSerializerRegistry>();
        if (registry.IsEmpty || services.GetService<IRedisConnector>() is not { } connector)
        {
            return Inactive;
        }

        options.Validate();
        var policies = services.GetRequiredService<ICachePolicyFactory>();
        var cacheOptions = services.GetRequiredService<IOptions<CacheOptions>>().Value;
        var loggers = services.GetRequiredService<ILoggerFactory>();
        var clock = services.GetRequiredService<TimeProvider>();
        var streams = cacheOptions.BroadcastEnabled
            ? services.GetServices<ITopicProvider>().OfType<RedisStreamsTopicProvider>().FirstOrDefault(p => p.Enabled)
            : null;
        var library = typeof(FormatNegotiation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        return new()
        {
            Negotiator = options.Mode == FormatNegotiationMode.Force
                ? null
                : new FormatNegotiator(NegotiationPlan.Desired(registry, policies), options, clock, services.GetRequiredService<ICachingTelemetryProvider>(), loggers.CreateLogger<FormatNegotiator>()),
            Advertisement = NegotiationPlan.Advertisement(registry, policies, new ValueReader(services.GetRequiredService<ISerializerProxy<byte[]>>(), registry), library),
            NodeId = (cacheOptions.SourceUri ?? CacheOptions.MachineUri).ToString().ToLowerInvariant(),
            Store = new NodeListStore(connector, NodeListStore.KeyFor(cacheOptions, redisOptions)),
            NodeList = new StreamNodeList(new RedisStreamGroupSource(connector), options),
            StreamKeys = streams is null ? null : streams.StreamKeys,
            Options = options,
            Clock = clock,
            Logger = loggers.CreateLogger<FormatNegotiationService>(),
        };
    }
}
