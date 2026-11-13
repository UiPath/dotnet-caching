using System.Collections.Concurrent;

namespace UiPath.Caching.Config;

/// <summary>The inner policies with background writes off, for writes of memory the caller reclaims on return, and with the JSON serializer only, since the values are the caller's own bytes.</summary>
internal sealed class AdapterPolicyFactory(ICachePolicyFactory inner) : ICachePolicyFactory
{
    internal static readonly CachePolicy Overlay = new()
    {
        BackgroundDistributedWrite = false,
        Serializer = new SerializerPolicy { Write = CacheValueFormats.JsonName, Read = [CacheValueFormats.JsonName], MigrateOnRead = false },
    };

    // Keyed on the inner instance, so a reloaded policy is merged again.
    private readonly ConcurrentDictionary<string, Merged> _named = new(StringComparer.Ordinal);
    private Merged? _default;

    public CachePolicy? Default
    {
        get
        {
            var source = inner.Default;
            var merged = Volatile.Read(ref _default);
            if (merged is null || !ReferenceEquals(merged.Source, source))
            {
                merged = new Merged(source, CachePolicyMerger.Merge(Overlay, source));
                Volatile.Write(ref _default, merged);
            }

            return merged.Policy;
        }
    }

    public IEnumerable<string> Keys => inner.Keys;

    public CachePolicy? Resolve(string policyName)
    {
        if (inner.Resolve(policyName) is not { } named)
        {
            return null;
        }

        if (!_named.TryGetValue(policyName, out var merged) || !ReferenceEquals(merged.Source, named))
        {
            _named[policyName] = merged = new Merged(named, CachePolicyMerger.Merge(Overlay, named));
        }

        return merged.Policy;
    }

    private sealed record Merged(CachePolicy? Source, CachePolicy? Policy);
}
