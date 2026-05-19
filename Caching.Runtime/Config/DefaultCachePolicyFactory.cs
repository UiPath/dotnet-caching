namespace UiPath.Platform.Caching.Config;

internal sealed class DefaultCachePolicyFactory : ICachePolicyFactory
{
    private readonly Dictionary<string, CachePolicy> _preMerged;
    private readonly CachePolicy _default;

    public DefaultCachePolicyFactory(
        IEnumerable<KeyValuePair<string, CachePolicy>>? policies,
        CachePolicy? defaultPolicy)
    {
        _default = defaultPolicy ?? CachePolicy.Empty;
        _preMerged = new Dictionary<string, CachePolicy>(StringComparer.OrdinalIgnoreCase);
        if (policies is null)
        {
            return;
        }
        foreach (var (key, policy) in policies)
        {
            if (!_preMerged.TryAdd(key, CachePolicyMerger.Merge(policy, _default)))
            {
                throw new InvalidOperationException(
                    $"CacheOptions.Policies contains keys that differ only by case (collision on '{key}'). Policy names are matched case-insensitively — rename them to be distinct.");
            }
        }
    }

    public CachePolicy Resolve(string policyName)
    {
        if (string.IsNullOrEmpty(policyName))
        {
            return _default;
        }
        return _preMerged.TryGetValue(policyName, out var policy) ? policy : _default;
    }

    public CachePolicy Default => _default;
}
