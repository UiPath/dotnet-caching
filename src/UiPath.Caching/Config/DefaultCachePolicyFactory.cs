using UiPath.Caching.Serialization;

namespace UiPath.Caching.Config;

internal sealed class DefaultCachePolicyFactory : ICachePolicyFactory
{
    private readonly Dictionary<string, CachePolicy> _preMerged;

    public DefaultCachePolicyFactory(
        IEnumerable<KeyValuePair<string, CachePolicy>>? policies,
        CachePolicy? defaultPolicy,
        TimeSpan distributedLockPollInterval = default,
        ValueSerializerRegistry? valueSerializers = null)
    {
        var registry = valueSerializers ?? ValueSerializerRegistry.Empty;
        Default = WithResolvedSerializer("default", defaultPolicy, registry);
        _preMerged = new Dictionary<string, CachePolicy>(StringComparer.OrdinalIgnoreCase);
        if (policies is not null)
        {
            foreach (var (key, policy) in policies)
            {
                var merged = WithResolvedSerializer(key, CachePolicyMerger.Merge(policy, defaultPolicy), registry);
                if (!_preMerged.TryAdd(key, merged))
                {
                    throw new InvalidOperationException(
                        $"CacheOptions.Policies contains keys that differ only by case (collision on '{key}'). Policy names are matched case-insensitively — rename them to be distinct.");
                }
            }
        }

        CachePolicyFactoryValidator.Validate(this, distributedLockPollInterval);
    }

    public CachePolicy? Default { get; }

    public IEnumerable<string> Keys => _preMerged.Keys;

    public CachePolicy? Resolve(string policyName)
    {
        if (string.IsNullOrEmpty(policyName))
        {
            return null;
        }

        return _preMerged.TryGetValue(policyName, out var policy) ? policy : null;
    }

    // Runs on the merged policy, so an inherited or overridden value is checked as it takes effect.
    [return: NotNullIfNotNull(nameof(policy))]
    private static CachePolicy? WithResolvedSerializer(string policyName, CachePolicy? policy, ValueSerializerRegistry registry)
    {
        if (policy?.Serializer is not { } serializer)
        {
            return policy;
        }

        if (serializer.Write is not null)
        {
            CheckName(policyName, nameof(SerializerPolicy.Write), serializer.Write, registry);
        }

        if (serializer.Read is { } read)
        {
            if (read.Count == 0)
            {
                Fail($"Cache policy '{policyName}' has an empty Serializer.Read, so it would read no format. Omit it to read every registered format.");
            }

            foreach (var name in read)
            {
                CheckName(policyName, nameof(SerializerPolicy.Read), name, registry);
            }

            var write = serializer.Write ?? CacheValueFormats.JsonName;
            if (!read.Contains(write, StringComparer.OrdinalIgnoreCase))
            {
                Fail($"Cache policy '{policyName}' writes '{write}' but Serializer.Read does not include it, so it would write values it refuses to read.");
            }
        }

        return serializer.Write is not null && registry.Resolve(serializer.Write) is { } writeSerializer
            ? CachePolicyMerger.WithWriteSerializer(policy, writeSerializer)
            : policy;
    }

    private static void CheckName(string policyName, string field, string? name, ValueSerializerRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Fail($"Cache policy '{policyName}' has a blank entry in Serializer.{field}. Name a registered serializer or \"json\", or omit the field to inherit.");
        }

        if (!registry.TryResolve(name, out _))
        {
            Fail(ValueSerializerRegistry.UnregisteredMessage($"Cache policy '{policyName}'", field, name));
        }
    }

    [DoesNotReturn]
    private static void Fail(string message) =>
        throw new OptionsValidationException(nameof(CacheOptions), typeof(CacheOptions), [message]);
}
