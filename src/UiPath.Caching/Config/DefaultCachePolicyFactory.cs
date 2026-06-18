namespace UiPath.Caching.Config;

internal sealed class DefaultCachePolicyFactory : ICachePolicyFactory
{
    private readonly Dictionary<string, CachePolicy> _preMerged;

    public DefaultCachePolicyFactory(
        IEnumerable<KeyValuePair<string, CachePolicy>>? policies,
        CachePolicy? defaultPolicy,
        TimeSpan distributedLockPollInterval = default)
    {
        Default = defaultPolicy;
        _preMerged = new Dictionary<string, CachePolicy>(StringComparer.OrdinalIgnoreCase);
        if (policies is not null)
        {
            foreach (var (key, policy) in policies)
            {
                if (!_preMerged.TryAdd(key, CachePolicyMerger.Merge(policy, defaultPolicy)))
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
}
