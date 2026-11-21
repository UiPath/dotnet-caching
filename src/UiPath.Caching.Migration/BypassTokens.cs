namespace UiPath.Caching.Migration;

/// <summary><c>byp</c> while the router is in <see cref="RouterMode.BypassLibrary"/>.</summary>
internal sealed class BypassTokens(RouterCore core) : IAdvertisementTokens
{
    public void AddTo(List<string> tokens)
    {
        if (core.Mode == RouterMode.BypassLibrary)
        {
            tokens.Add(RouterAdvertisement.Bypass);
        }
    }
}
