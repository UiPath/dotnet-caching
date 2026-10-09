using Microsoft.Extensions.Caching.Memory;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>A real <see cref="MemoryCache"/> whose lookups, by key and by span alike, are counted.</summary>
internal sealed class CountingMemoryCacheFactory : IMemoryCacheFactory
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { TrackStatistics = true });

    public long Lookups
    {
        get
        {
            var statistics = _cache.GetCurrentStatistics()!;
            return statistics.TotalHits + statistics.TotalMisses;
        }
    }

    public IMemoryCache Get(IMemoryCacheOptions memoryOptions) => _cache;
}
