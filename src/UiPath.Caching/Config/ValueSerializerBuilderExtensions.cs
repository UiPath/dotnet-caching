using UiPath.Caching.Serialization;

namespace UiPath.Caching.Config;

public static class ValueSerializerBuilderExtensions
{
    /// <summary>Makes <paramref name="name"/> selectable as a policy's serializer; the container owns what <paramref name="factory"/> returns and disposes it, async or sync, when it is disposed.</summary>
    /// <remarks>
    /// <paramref name="factory"/> must return a new instance, never one resolved from the container: the registry disposes it. It runs when the registry is built, on first cache use;
    /// a build that throws disposes what it created, and a later use runs it again. The serializer's ids must be consumer ids (0x10-0x1E, 0x100-0x1FF).
    /// A name used twice on one builder throws here; the ids and the policies' names are checked when the policy factory is first resolved, on first cache use.
    /// </remarks>
    public static ICachingBuilder AddValueSerializer(
        this ICachingBuilder builder,
        string name,
        Func<IServiceProvider, ICacheValueSerializer> factory) =>
        Add(builder, name, factory, library: false);

    internal static ICachingBuilder AddLibraryValueSerializer(
        this ICachingBuilder builder,
        string name,
        Func<IServiceProvider, ICacheValueSerializer> factory) =>
        Add(builder, name, factory, library: true);

    private static ICachingBuilder Add(ICachingBuilder builder, string name, Func<IServiceProvider, ICacheValueSerializer> factory, bool library)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        if (string.Equals(name, CacheValueFormats.JsonName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{name}' is reserved for the built-in JSON format.", nameof(name));
        }

        if (builder.Services.Any(d => d.ImplementationInstance is ValueSerializerRegistration { Forwarded: false } existing
            && string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"A value serializer named '{name}' is already registered.", nameof(name));
        }

        builder.Services.AddSingleton(new ValueSerializerRegistration(name, factory, library));
        return builder;
    }
}
