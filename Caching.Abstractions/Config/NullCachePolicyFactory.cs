namespace UiPath.Platform.Caching.Config;

public sealed class NullCachePolicyFactory : ICachePolicyFactory
{
    public static readonly NullCachePolicyFactory Instance = new();

    private NullCachePolicyFactory() { }

    public CachePolicy Resolve(string policyName) => CachePolicy.Empty;

    public CachePolicy Default => CachePolicy.Empty;
}
