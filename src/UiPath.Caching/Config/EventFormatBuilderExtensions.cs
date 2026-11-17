using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Config;

public static class EventFormatBuilderExtensions
{
    private static readonly object InnerJsonKey = new();

    /// <summary>Reads event bodies that start with byte <paramref name="formatId"/>; <paramref name="write"/> true writes them once every live node can read them.</summary>
    /// <remarks>
    /// <paramref name="formatId"/> is one byte, 0x01-0x0F (except 0x09, 0x0A, 0x0D) or 0x10-0x1E, and <c>Encode</c> must start every body with it: a body that does not is written as JSON and logged once.
    /// Everything is checked here: a name or an id used twice throws, as does a second format with <paramref name="write"/> true, and a failing call registers nothing.
    /// The JSON decoder stays, whether <c>AddCloudEvents</c> is called before or after this, so a restarted node still reads a JSON backlog. Every registered format decodes, at most one writes.
    /// With <paramref name="write"/> true and <c>FormatNegotiation.Mode</c> <c>Auto</c> the node writes JSON until the node list shows that every node reads the format, and again as soon as one does not;
    /// <c>Force</c> writes it at once. <paramref name="factory"/> runs when the formatter is first resolved; the container owns what it returns and disposes it.
    /// </remarks>
    public static ICachingBuilder AddEventFormat(
        this ICachingBuilder builder,
        string name,
        int formatId,
        Func<IServiceProvider, IEventFormatterProxy<ICacheEvent>> factory,
        bool write)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        var services = builder.Services;
        var registry = services.FirstOrDefault(d => d.ServiceType == typeof(EventFormatRegistry))?.ImplementationInstance as EventFormatRegistry;
        var isNew = registry is null;
        registry ??= new EventFormatRegistry();

        var format = registry.Add(name, formatId, factory, write);

        if (isNew)
        {
            services.AddSingleton(registry);
            services.AddSingleton<IEventWrites>(sp => new LazyEventWrites(registry, sp.GetService<FormatNegotiation>));
            // Once per collection, since every AddCaching call has its own builder and a second wrap would wrap the composite; it reads the registry when resolved, so later formats are in it.
            // At completion, once every registration that supplies the JSON formatter has run, whichever order they were called in.
            builder.RegisterOnCompleteCallback(typeof(EventFormatBuilderExtensions), b => WrapJsonFormatter(b.Services));
        }

        services.AddKeyedSingleton(format, (sp, _) => factory(sp));
        return builder;
    }

    private static void WrapJsonFormatter(IServiceCollection services)
    {
        var registry = (EventFormatRegistry)services.First(d => d.ServiceType == typeof(EventFormatRegistry)).ImplementationInstance!;
        var json = services.LastOrDefault(d => d.ServiceType == typeof(IEventFormatterProxy<ICacheEvent>) && !d.IsKeyedService);
        if (json is not null)
        {
            services.Remove(json);
            // Re-registered under a key, so the container still creates and disposes it; an instance stays its owner's.
            services.Add(Keyed(json));
        }

        services.AddSingleton<IEventFormatterProxy<ICacheEvent>>(sp => new CompositeEventFormatter(
            json is null ? new CacheEventFormatter() : sp.GetRequiredKeyedService<IEventFormatterProxy<ICacheEvent>>(InnerJsonKey),
            [.. registry.Formats.Select(f => (f.Id, f.Name, sp.GetRequiredKeyedService<IEventFormatterProxy<ICacheEvent>>(f), f.Write))],
            sp.GetRequiredService<IEventWrites>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<CompositeEventFormatter>()));
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
