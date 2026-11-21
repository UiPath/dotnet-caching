namespace UiPath.Caching.Migration;

/// <summary><c>ap=&lt;app&gt;</c>: the application the entry belongs to, so applications that share a node hash do not count each other's nodes.</summary>
internal sealed class ScopeTokens(string? scope) : IAdvertisementTokens
{
    public static string? For(string? appShortName) =>
        string.IsNullOrWhiteSpace(appShortName) ? null : Uri.EscapeDataString(appShortName.Trim().ToLowerInvariant());

    public void AddTo(List<string> tokens)
    {
        if (scope is not null)
        {
            tokens.Add(RouterAdvertisement.ScopePrefix + scope);
        }
    }
}