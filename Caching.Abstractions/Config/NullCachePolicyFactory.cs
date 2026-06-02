namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public sealed class NullCachePolicyFactory : ICachePolicyFactory
{
    public static readonly NullCachePolicyFactory Instance = new();

    private NullCachePolicyFactory() { }

    public CachePolicy? Resolve(string policyName) => default;

    public CachePolicy? Default => null;

    public IEnumerable<string> Keys => Array.Empty<string>();
}
