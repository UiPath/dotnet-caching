using UiPath.Caching.Serialization;

namespace UiPath.Caching.Negotiation;

/// <summary>What this node can write and what it can read, from its registered serializers and policies.</summary>
internal static class NegotiationPlan
{
    public const string DefaultPolicyName = "default";

    /// <summary>One entry per registered serializer, so a policy built at the call site that names one is held to the same decision as a registered one.</summary>
    public static IReadOnlyList<DesiredWrite> Desired(ValueSerializerRegistry registry, ICachePolicyFactory policies)
    {
        var named = Policies(policies).ToList();
        var desired = new List<DesiredWrite>();
        foreach (var (_, writer, _) in registry.Entries)
        {
            desired.Add(new(
                writer,
                [.. registry.FormatIdsOf(writer).ToArray().Select(NodeAdvertisement.Token)],
                [.. named.Where(p => ReferenceEquals(p.Policy.WriteSerializer, writer)).Select(p => p.Name.ToLowerInvariant())],
                [.. named.Where(p => !ReferenceEquals(p.Policy.WriteSerializer, writer)).Select(p => p.Name.ToLowerInvariant())]));
        }

        return desired;
    }

    /// <param name="readsBinaryEvents">Whether the binary event decoder is registered; a node without it still lists the JSON events it reads.</param>
    public static NodeAdvertisement Advertisement(ValueSerializerRegistry registry, ICachePolicyFactory policies, ValueReader reader, string library, bool readsBinaryEvents = false)
    {
        var reads = new List<string> { NodeAdvertisement.Json };
        foreach (var (_, serializer, _) in registry.Entries)
        {
            reads.AddRange(registry.FormatIdsOf(serializer).ToArray().Select(NodeAdvertisement.Token));
        }

        var strict = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (name, policy) in Policies(policies))
        {
            if (!reader.SniffsFor(policy))
            {
                // Its reads never look for a header, so a value in another format would be handed to it as bytes or fail: it reads JSON only.
                strict[name.ToLowerInvariant()] = [NodeAdvertisement.Json, NodeAdvertisement.NoSniff];
            }
            else if (policy.Serializer?.Read is { } read)
            {
                strict[name.ToLowerInvariant()] = [.. read.SelectMany(format => Tokens(registry, format)).Distinct()];
            }
        }

        if (policies.Default is null && !reader.SniffsFor(null))
        {
            strict[DefaultPolicyName] = [NodeAdvertisement.Json, NodeAdvertisement.NoSniff];
        }

        return new(library, Guid.NewGuid().ToString("N"), reads.Distinct().ToList(), strict, EventReads: readsBinaryEvents ? [NodeAdvertisement.Json, BinaryEventSettings.Token] : [NodeAdvertisement.Json]);
    }

    /// <summary>The policies whose read list leaves JSON out, which a node still in Auto writes while it holds a format back.</summary>
    public static IReadOnlyList<string> ReadingNoJson(ICachePolicyFactory policies) =>
        [.. Policies(policies)
            .Where(p => p.Policy.Serializer?.Read is { } read && !read.Contains(CacheValueFormats.JsonName, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Name)];

    private static IEnumerable<string> Tokens(ValueSerializerRegistry registry, string format) =>
        registry.Resolve(format) is { } serializer
            ? registry.FormatIdsOf(serializer).ToArray().Select(NodeAdvertisement.Token)
            : [NodeAdvertisement.Json];

    private static IEnumerable<(string Name, CachePolicy Policy)> Policies(ICachePolicyFactory policies)
    {
        if (policies.Default is { } fallback)
        {
            yield return (DefaultPolicyName, fallback);
        }

        foreach (var name in policies.Keys)
        {
            if (policies.Resolve(name) is { } policy)
            {
                yield return (name, policy);
            }
        }
    }
}
