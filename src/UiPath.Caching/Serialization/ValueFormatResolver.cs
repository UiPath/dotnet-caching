using System.Collections.Concurrent;
using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Serialization;

/// <summary>Decides per call which serializer a write uses and whether a read looks for a format header.</summary>
internal sealed class ValueFormatResolver
{
    private readonly ValueSerializerRegistry _registry;
    private readonly CachePolicy _defaultPolicy;
    private readonly IEffectiveFormat? _effective;
    private readonly ConcurrentDictionary<ReadKey, ReadableFormats> _readable = new(ReadKeyComparer.Instance);
    private readonly ConcurrentDictionary<ICacheValueSerializer, ReadableFormats> _anyMigrating = new(ReferenceEqualityComparer.Instance);
    private ReadableFormats? _defaultReadable;
    private ReadableFormats? _defaultMigrating;

    public ValueFormatResolver(ISerializerProxy<byte[]> proxy, ValueSerializerRegistry registry, CachePolicy defaultPolicy, IEffectiveFormat? effective = null)
    {
        _registry = registry;
        _defaultPolicy = defaultPolicy;
        _effective = effective;
        Reader = registry.IsEmpty ? null : new ValueReader(proxy, registry);
    }

    /// <summary>Null while no serializer is registered: nothing then reads by header.</summary>
    public ValueReader? Reader { get; }

    /// <summary>A write the node node list does not yet allow is JSON. A call without a policy takes the default policy's, and a policy that sets no write serializer falls back to the default's. A write that names a serializer nobody registered throws. A write that does not also read takes no read list, so a bad one fails the read, not the write.</summary>
    public ValueFormat For(CachePolicy? policy, bool forWrite, bool alsoReads = false)
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

        return new(writer, Reader is { } reader && reader.SniffsFor(effective) ? (forWrite && !alsoReads ? ReadableFormats.Any : ReadableFor(policy, effective)) : null);
    }

    // A policy that sets no read list takes the default policy's, as it takes its write. The default's own set is built once, so a call with no
    // policy, or with one that only sets other fields, costs a field read; any other is found by what it lists and writes, not by the policy object,
    // so a policy made per call neither allocates nor grows the table.
    private ReadableFormats ReadableFor(CachePolicy? policy, CachePolicy effective)
    {
        var defaultRead = _defaultPolicy.Serializer?.Read;
        var read = policy?.Serializer?.Read ?? defaultRead;
        var migrateTo = MigrationTarget(policy, effective);
        if (read is null)
        {
            return migrateTo is null ? ReadableFormats.Any : AnyMigratingTo(migrateTo);
        }

        if (ReferenceEquals(effective, _defaultPolicy) && ReferenceEquals(read, defaultRead))
        {
            return migrateTo is null
                ? _defaultReadable ??= Resolve(read, _defaultPolicy, null)
                : _defaultMigrating ??= Resolve(read, _defaultPolicy, migrateTo);
        }

        var key = new ReadKey(read, ConfiguredWriter(effective), migrateTo is not null);
        if (_readable.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resolved = Resolve(read, effective, migrateTo);
        return _readable.GetOrAdd(key.Snapshot(), resolved);
    }

    // A policy that sets MigrateOnRead sets it for itself; one that does not takes the default's, as it takes its Read. A policy that writes JSON has no target: nothing is rewritten toward it.
    private ICacheValueSerializer? MigrationTarget(CachePolicy? policy, CachePolicy effective) =>
        (policy?.Serializer?.MigrateOnRead ?? _defaultPolicy.Serializer?.MigrateOnRead) == true ? ConfiguredWriter(effective) : null;

    private ReadableFormats AnyMigratingTo(ICacheValueSerializer migrateTo)
    {
        if (_anyMigrating.TryGetValue(migrateTo, out var cached))
        {
            return cached;
        }

        return _anyMigrating.GetOrAdd(migrateTo, static target => ReadableFormats.AnyMigratingTo(target));
    }

    private ReadableFormats Resolve(IReadOnlyList<string> read, CachePolicy effective, ICacheValueSerializer? migrateTo) =>
        // The configured format is readable even while the node list holds it back, and then the write is JSON.
        ReadableFormats.Resolve(_registry, read, ConfiguredWriter(effective), jsonFallback: _effective is not null && Reader is { JsonProxyIsDefault: true }, migrateTo);

    private ICacheValueSerializer? ConfiguredWriter(CachePolicy effective) =>
        effective.WriteSerializer
            ?? (effective.Serializer?.Write is { } name && _registry.TryResolve(name, out var serializer) ? serializer : null);

    // Per field, as PolicyDuration does, without building a merged policy: the one that supplies Write supplies its resolved slot too.
    private CachePolicy WriteSource(CachePolicy? policy) =>
        policy is null || (policy.Serializer?.Write is null && policy.WriteSerializer is null) ? _defaultPolicy : policy;

    private ICacheValueSerializer? WriterOf(CachePolicy effective) => _registry.WriteSerializerFor(effective);

    private readonly record struct ReadKey(IReadOnlyList<string> Read, ICacheValueSerializer? Writer, bool Migrate)
    {
        public ReadKey Snapshot() => new([.. Read], Writer, Migrate);
    }

    // By what the list says, so a copy of it, or one edited since it was first read, is not mistaken for it.
    private sealed class ReadKeyComparer : IEqualityComparer<ReadKey>
    {
        public static ReadKeyComparer Instance { get; } = new();

        public bool Equals(ReadKey x, ReadKey y)
        {
            if (!ReferenceEquals(x.Writer, y.Writer) || x.Migrate != y.Migrate || x.Read.Count != y.Read.Count)
            {
                return false;
            }

            for (var i = 0; i < x.Read.Count; i++)
            {
                if (!string.Equals(x.Read[i], y.Read[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(ReadKey key)
        {
            var hash = new HashCode();
            hash.Add(key.Writer is null ? 0 : RuntimeHelpers.GetHashCode(key.Writer));
            hash.Add(key.Migrate);
            for (var i = 0; i < key.Read.Count; i++)
            {
                hash.Add(key.Read[i], StringComparer.OrdinalIgnoreCase);
            }

            return hash.ToHashCode();
        }
    }
}
