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

    /// <summary>The channels this node's topics listen on, and which are confirmed; <see langword="null"/> when the app has no Pub/Sub transport.</summary>
    public IPubSubSubscriptions? PubSub { get; init; }

    public PubSubNodeList? Subscribers { get; init; }

    public FormatNegotiationOptions Options { get; init; } = new();

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public ILogger Logger { get; init; } = NullLogger.Instance;

    public bool IsActive => Advertisement is not null;

    /// <summary>The node reads the node list off a transport it can see; otherwise only the operator's word can switch a format.</summary>
    public bool HasTransport => StreamKeys is not null || PubSub is not null;

    public static FormatNegotiation Create(IServiceProvider services)
    {
        var redisOptions = services.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
        var options = redisOptions.FormatNegotiation;
        var registry = services.GetRequiredService<ValueSerializerRegistry>();
        var events = services.GetService<BinaryEventSettings>();
        if (registry.IsEmpty && events is null)
        {
            return Inactive;
        }

        if (services.GetService<IRedisConnector>() is not { } connector)
        {
            return events is { Write: true } ? WithoutNodeList(services, options) : Inactive;
        }

        options.Validate();
        var policies = services.GetRequiredService<ICachePolicyFactory>();
        var cacheOptions = services.GetRequiredService<IOptions<CacheOptions>>().Value;
        var loggers = services.GetRequiredService<ILoggerFactory>();
        var clock = services.GetRequiredService<TimeProvider>();
        var streams = cacheOptions.BroadcastEnabled
            ? services.GetServices<ITopicProvider>().OfType<RedisStreamsTopicProvider>().FirstOrDefault(p => p.Enabled)
            : null;
        var pubSub = cacheOptions.BroadcastEnabled
            ? services.GetServices<ITopicProvider>().OfType<RedisPubSubTopicProvider>().FirstOrDefault(p => p.Enabled)
            : null;
        if (options.Mode == FormatNegotiationMode.Force && NegotiationPlan.ReadingNoJson(policies) is { Count: > 0 } readingNoJson)
        {
            loggers.CreateLogger<FormatNegotiationService>().LogWarning(
                "Serializer.Read of {Policies} leaves out json, and FormatNegotiation.Mode is Force. A node still in Auto writes JSON until every node reads its format, and this node reads those values as misses. Keep every node on the shared keys in Force, or add json to Read.",
                string.Join(", ", readingNoJson.Select(name => $"'{name}'")));
        }

        var library = typeof(FormatNegotiation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        return new()
        {
            // A node with nothing to decide, in Force or with no serializer and no binary events to write, only advertises.
            Negotiator = options.Mode == FormatNegotiationMode.Force || (registry.IsEmpty && events is not { Write: true })
                ? null
                : new FormatNegotiator(
                    NegotiationPlan.Desired(registry, policies),
                    options,
                    clock,
                    services.GetRequiredService<ICachingTelemetryProvider>(),
                    loggers.CreateLogger<FormatNegotiator>(),
                    events is { Write: true } ? new DesiredEvents([BinaryEventSettings.Token]) : null),
            Advertisement = NegotiationPlan.Advertisement(registry, policies, new ValueReader(services.GetRequiredService<ISerializerProxy<byte[]>>(), registry), library, readsBinaryEvents: events is not null),
            NodeId = (cacheOptions.SourceUri ?? CacheOptions.MachineUri).ToString().ToLowerInvariant(),
            Store = new NodeListStore(connector, NodeListStore.KeyFor(cacheOptions, redisOptions)),
            NodeList = new StreamNodeList(new RedisStreamGroupSource(connector), options),
            StreamKeys = streams is null ? null : streams.StreamKeys,
            PubSub = pubSub,
            Subscribers = pubSub is null ? null : new PubSubNodeList(new RedisSubscriberSource(connector)),
            Options = options,
            Clock = clock,
            Logger = loggers.CreateLogger<FormatNegotiationService>(),
        };
    }

    /// <summary>
    /// Whether this node writes binary events: never when it is not set to, as configured in <see cref="FormatNegotiationMode.Force"/>, as the node list allows
    /// in <see cref="FormatNegotiationMode.Auto"/>, and with no node list to ask only on the operator's word.
    /// </summary>
    public IBinaryEvents EventWrites(BinaryEventSettings settings) =>
        !settings.Write ? FixedBinaryEvents.Never : Negotiator is { } negotiator ? negotiator : IsActive || Options.Mode == FormatNegotiationMode.Force ? FixedBinaryEvents.Always : FixedBinaryEvents.Never;

    // No Redis connection, so no node list and nothing to advertise to: the node writes JSON events until the operator attests, or forces them.
    private static FormatNegotiation WithoutNodeList(IServiceProvider services, FormatNegotiationOptions options)
    {
        options.Validate();
        var clock = services.GetRequiredService<TimeProvider>();
        var loggers = services.GetRequiredService<ILoggerFactory>();
        return new()
        {
            Negotiator = options.Mode == FormatNegotiationMode.Force
                ? null
                : new FormatNegotiator([], options, clock, services.GetRequiredService<ICachingTelemetryProvider>(), loggers.CreateLogger<FormatNegotiator>(), new DesiredEvents([BinaryEventSettings.Token])),
            Options = options,
            Clock = clock,
            Logger = loggers.CreateLogger<FormatNegotiationService>(),
        };
    }
}
