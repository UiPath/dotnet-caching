using UiPath.Caching.Negotiation;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Serialization;

/// <summary>What a cache needs to rewrite a value it read: the queue, the check, and the node list's word on what may be written now.</summary>
internal sealed class ValueMigration(FormatMigrator migrator, ValueRewriter rewriter, ValueSerializerRegistry registry, IEffectiveFormat? effective) : IDisposable
{
    /// <summary>The most a value may weigh to be offered: a rewrite sends it back to the server to compare, and the script hashes it there.</summary>
    internal const int MaxValueBytes = 256 * 1024;

    /// <summary>The most one hash rewrite carries, whatever its fields weigh singly.</summary>
    internal const int MaxJobBytes = 1024 * 1024;

    public FormatMigrator Migrator => migrator;

    public ValueRewriter Rewriter => rewriter;

    /// <summary>Null for an app with no serializer registered: nothing there is ever rewritten.</summary>
    public static ValueMigration? Create(IServiceProvider services)
    {
        var registry = services.GetRequiredService<ValueSerializerRegistry>();
        if (registry.IsEmpty)
        {
            return null;
        }

        var migrator = new FormatMigrator(
            services.GetRequiredService<ICachingTelemetryProvider>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger<FormatMigrator>());
        return new(migrator, new ValueRewriter(services.GetRequiredService<ISerializerProxy<byte[]>>(), registry), registry, services.GetService<FormatNegotiation>()?.Negotiator);
    }

    /// <summary>
    /// Whether a value stored as <paramref name="stored"/> is to be rewritten into <paramref name="target"/>: only while the node list lets the target be written,
    /// so a node holding the format back at JSON rewrites nothing, and only from a format the target's serializer does not own.
    /// </summary>
    public bool Wants(ICacheValueSerializer target, ReadOnlySpan<byte> stored)
    {
        if (stored.IsEmpty || stored.Length > MaxValueBytes || !Allows(target))
        {
            return false;
        }

        if (!ValueHeader.TryRead(stored, out var formatId, out _))
        {
            // JSON that cannot be compared would only be looked at and left, every window.
            return rewriter.ComparesJson;
        }

        return registry.TryGetByFormat(formatId, out var source) && !ReferenceEquals(source, target);
    }

    /// <summary>The rewriter's check; a target serializer that throws for the value counts as a value that would not come out the same, logged like a failed rewrite.</summary>
    public bool TryReencode<T>(byte[] original, ICacheValueSerializer target, out SerializedPayload payload)
    {
        try
        {
            return rewriter.TryReencode<T>(original, target, out payload);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            migrator.NoteFailure(ex);
            payload = default;
            return false;
        }
    }

    /// <summary>Whether the node list still lets this node write <paramref name="target"/>; asked again by a job right before it writes, since it may have waited.</summary>
    public bool Allows(ICacheValueSerializer target) => effective?.Allows(target) != false;

    public void Dispose() => migrator.Dispose();
}
