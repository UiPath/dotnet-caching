namespace UiPath.Caching;

/// <summary>The InMemory provider: no distributed tier.</summary>
internal sealed class NoInnerTier : InnerTier
{
    internal NoInnerTier()
        : base(false, null, null, default, null)
    {
    }
}
