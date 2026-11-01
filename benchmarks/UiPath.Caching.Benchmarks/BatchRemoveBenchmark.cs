using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>Removing 10 keys on the memory tier: one batch against single removes.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class BatchRemoveBenchmark
{
    private const int Count = 10;

    private IHost _host = default!;
    private ICache<string> _cache = default!;
    private CacheKey[] _keys = default!;

    [GlobalSetup]
    public async Task Setup()
    {
        _host = HostHelper.GetHost(0, cache: KnownCacheProviderNames.InMemory);
        await _host.StartAsync();
        _cache = _host.Services.GetRequiredService<ICache<string>>();
        _keys = Enumerable.Range(0, Count).Select(i => (CacheKey)$"bench:remove:{i}").ToArray();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<IReadOnlyList<CacheKey>> RemoveMany() => _cache.RemoveManyAsync(_keys);

    [Benchmark]
    public async Task<bool> SingleRemoves()
    {
        var all = true;
        foreach (var key in _keys)
        {
            all &= await _cache.RemoveAsync(key);
        }

        return all;
    }
}
