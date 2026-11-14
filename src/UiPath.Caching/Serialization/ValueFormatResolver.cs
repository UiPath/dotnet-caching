using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Serialization;

/// <summary>Decides per call which serializer a write uses and whether a read looks for a format header.</summary>
internal sealed class ValueFormatResolver
{
    private readonly ValueSerializerRegistry _registry;
    private readonly CachePolicy _defaultPolicy;
    private readonly IEffectiveFormat? _effective;

    public ValueFormatResolver(ISerializerProxy<byte[]> proxy, ValueSerializerRegistry registry, CachePolicy defaultPolicy, IEffectiveFormat? effective = null)
    {
        _registry = registry;
        _defaultPolicy = defaultPolicy;
        _effective = effective;
        Reader = registry.IsEmpty ? null : new ValueReader(proxy, registry);
    }

    /// <summary>Null while no serializer is registered: nothing then reads by header.</summary>
    public ValueReader? Reader { get; }

    /// <summary>A write the node node list does not yet allow is JSON. A call without a policy takes the default policy's, and a policy that sets no write serializer falls back to the default's. A write that names a serializer nobody registered throws.</summary>
    public ValueFormat For(CachePolicy? policy, bool forWrite)
    {
        var effective = WriteSource(policy);
        if (Reader is null && effective.Serializer is null)
        {
            return default;
        }

        var writer = forWrite ? WriterOf(effective) : null;

        // The policy still names the format, so a read still looks for headers; only the write is held to what every node reads.
        // A custom proxy has no JSON to fall back to: what it writes can read back as a header, so such a policy always writes its own format.
        if (writer is not null && _effective is { } gate && Reader is { JsonProxyIsDefault: true } && !gate.Allows(writer))
        {
            writer = null;
        }

        return new(writer, Reader is { } reader && reader.SniffsFor(effective));
    }

    // Per field, as PolicyDuration does, without building a merged policy: the one that supplies Write supplies its resolved slot too.
    private CachePolicy WriteSource(CachePolicy? policy) =>
        policy is null || (policy.Serializer?.Write is null && policy.WriteSerializer is null) ? _defaultPolicy : policy;

    private ICacheValueSerializer? WriterOf(CachePolicy effective) => _registry.WriteSerializerFor(effective);
}
