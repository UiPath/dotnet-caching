namespace UiPath.Caching;

/// <summary>The keys a rehydration adopted from the inner tier, known to the coordinator even when the generator then fails.</summary>
internal sealed class RehydrateRun
{
    private readonly List<CacheKey> _adopted = [];

    public IReadOnlyList<CacheKey> Adopted => _adopted;

    public void Adopt(CacheKey key) => _adopted.Add(key);
}
