using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.Negotiation;

namespace UiPath.Caching.MessagePack;

public static class MessagePackCachingBuilderExtensions
{
    public const string DefaultName = "messagepack";

    private static readonly object InnerJsonKey = new();

    /// <summary>Registers the MessagePack serializer as <paramref name="name"/>, selectable per policy through <c>Serializer.Write</c>.</summary>
    public static ICachingBuilder AddMessagePackSerializer(
        this ICachingBuilder builder,
        string name = DefaultName,
        Action<MessagePackValueSerializerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new MessagePackValueSerializerOptions();
        configure?.Invoke(options);
        return builder.AddLibraryValueSerializer(name, _ => new MessagePackValueSerializer(options));
    }

    /// <summary>Reads binary and JSON events; <paramref name="write"/> true writes binary once every live node can read it, false always writes JSON.</summary>
    /// <remarks>
    /// Wraps the JSON formatter that would otherwise be registered, whether <c>AddCloudEvents</c> is called before or after it, so the JSON
    /// wire is what it was. Keep the JSON decoder: a restarted node replays a stream backlog that may hold JSON. A second call replaces the first one's setting.
    /// With <c>write: true</c> and <c>FormatNegotiation.Mode</c> <c>Auto</c> the node writes JSON until the node list shows that every node reads binary
    /// events, and again as soon as one does not; <c>Force</c> writes binary at once.
    /// </remarks>
    public static ICachingBuilder AddMessagePackEvents(this ICachingBuilder builder, bool write)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Services.FirstOrDefault(d => d.ServiceType == typeof(BinaryEventSettings))?.ImplementationInstance is BinaryEventSettings existing)
        {
            existing.Write = write;
            return builder;
        }

        var settings = new BinaryEventSettings(write);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<IBinaryEvents>(sp => new LazyBinaryEvents(settings, sp.GetService<FormatNegotiation>));
        // At completion, once every registration that supplies the JSON formatter has run, whichever order they were called in.
        builder.RegisterOnCompleteCallback(typeof(MessagePackCachingBuilderExtensions), b => WrapJsonFormatter(b.Services));
        return builder;
    }

    private static void WrapJsonFormatter(IServiceCollection services)
    {
        var json = services.LastOrDefault(d => d.ServiceType == typeof(IEventFormatterProxy<ICacheEvent>) && !d.IsKeyedService);
        if (json is not null)
        {
            services.Remove(json);
            // Re-registered under a key, so the container still creates and disposes it; an instance stays its owner's.
            services.Add(Keyed(json));
        }

        services.AddSingleton<IEventFormatterProxy<ICacheEvent>>(sp => new CompositeEventFormatter(
            json is null ? new CacheEventFormatter() : sp.GetRequiredKeyedService<IEventFormatterProxy<ICacheEvent>>(InnerJsonKey),
            new MessagePackEventFormatter(),
            sp.GetRequiredService<IBinaryEvents>()));
    }

    private static ServiceDescriptor Keyed(ServiceDescriptor descriptor)
    {
        var type = typeof(IEventFormatterProxy<ICacheEvent>);
        if (descriptor.ImplementationInstance is { } instance)
        {
            return new ServiceDescriptor(type, InnerJsonKey, instance);
        }

        return descriptor.ImplementationFactory is { } factory
            ? ServiceDescriptor.DescribeKeyed(type, InnerJsonKey, (sp, _) => factory(sp), ServiceLifetime.Singleton)
            : ServiceDescriptor.DescribeKeyed(type, InnerJsonKey, descriptor.ImplementationType!, ServiceLifetime.Singleton);
    }
}
