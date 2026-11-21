using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Migration;

/// <summary>The Phase B tokens (<c>pb</c>, <c>pbg</c>, <c>ca</c>, <c>fl</c>, the acknowledged identity, <c>pi</c>, <c>rc</c>) in the router's entry.</summary>
internal sealed class PhaseBTokens(PhaseBAdvertTokens tokens) : IAdvertisementTokens
{
    public void AddTo(List<string> list) => tokens.AddTo(list);
}
