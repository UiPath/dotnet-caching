using UiPath.Caching.Serialization;

namespace UiPath.Caching.Config;

public static class ValueSerializerBuilderExtensions
{
    /// <summary>Makes <paramref name="name"/> selectable as a policy's serializer; the container owns what <paramref name="factory"/> returns and disposes it, async or sync, when it is disposed.</summary>
    /// <remarks>
    /// <paramref name="formatIds"/> are checked here, not when the factory runs: 0x01-0x0F (except 0x09, 0x0A, 0x0D), 0x10-0x1E or 0x100-0x1FF, all of one header length,
    /// none claimed by another serializer. A failing call registers nothing. The serializer's <c>FormatIds</c> must equal them as a set; that is checked when the registry is built.
    /// <paramref name="factory"/> must return a new instance, never one resolved from the container: the registry disposes it. It runs when the registry is built, on first cache use;
    /// a build that throws disposes what it created, and a later use runs it again.
    /// A name used twice on one builder throws here; the policies' names are checked when the policy factory is first resolved, on first cache use.
    /// </remarks>
    public static ICachingBuilder AddValueSerializer(
        this ICachingBuilder builder,
        string name,
        IReadOnlyList<int> formatIds,
        Func<IServiceProvider, ICacheValueSerializer> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(formatIds);
        ArgumentNullException.ThrowIfNull(factory);

        if (string.Equals(name, CacheValueFormats.JsonName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{name}' is reserved for the built-in JSON format.", nameof(name));
        }

        var existing = builder.Services.Select(d => d.ImplementationInstance).OfType<ValueSerializerRegistration>().ToList();
        if (existing.Any(r => !r.Forwarded && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"A value serializer named '{name}' is already registered.", nameof(name));
        }

        var ids = Array.AsReadOnly([.. formatIds]);
        var fault = FormatIdRules.Find(name, ids, id => existing.FirstOrDefault(r => !IsReplaced(r) && r.Ids.Contains(id))?.Name);
        if (fault is { } found)
        {
            throw found.OutOfRange
                ? new ArgumentOutOfRangeException(nameof(formatIds), found.Message)
                : new ArgumentException(found.Message, nameof(formatIds));
        }

        builder.Services.AddSingleton(new ValueSerializerRegistration(name, ids, factory));
        return builder;

        bool IsReplaced(ValueSerializerRegistration r) =>
            r.Forwarded && (SameName(r.Name, name) || existing.Any(o => !o.Forwarded && SameName(o.Name, r.Name)));
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
