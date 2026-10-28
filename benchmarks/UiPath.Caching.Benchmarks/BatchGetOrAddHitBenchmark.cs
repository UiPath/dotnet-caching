using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>A 100-key batch GetOrAdd where every key is already in the local tier.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class BatchGetOrAddHitBenchmark
{
    private const int Count = 100;

    private readonly string _run = Guid.NewGuid().ToString("N");

    private IHost _host = default!;
    private ICache<string> _cache = default!;
    private KeyValuePair<CacheKey, int>[] _entries = default!;

    [Params(KnownCacheProviderNames.InMemory, KnownCacheProviderNames.InMemoryRedis)]
    public string Provider { get; set; } = default!;

    [GlobalSetup]
    public async Task Setup()
    {
        var extra = new Dictionary<string, string?> { { "Caching:Connections:Redis:ConnectionString", "127.0.0.1:6379,abortConnect=false" } };
        _host = HostHelper.GetHost(0, cache: Provider, topic: "RedisPubSub", extra: extra);
        await _host.StartAsync();
        _cache = _host.Services.GetRequiredService<ICache<string>>();
        _entries = new KeyValuePair<CacheKey, int>[Count];
        for (var i = 0; i < Count; i++)
        {
            _entries[i] = new($"bench:batch:{_run}:{i}", i);
            await _cache.SetAsync(_entries[i].Key, $"v{i}", TimeSpan.FromMinutes(30));
        }

        var warm = await _cache.GetOrAddAsync(_entries, static (s, ct) => throw new InvalidOperationException("miss"));
        if (warm.Length != Count)
        {
            throw new InvalidOperationException("seed");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        foreach (var e in _entries)
        {
            await _cache.RemoveAsync(e.Key);
        }

        await _host.StopAsync();
        _host.Dispose();
    }

    [Benchmark]
    public async Task<string?> SingleGets()
    {
        string? last = null;
        foreach (var e in _entries)
        {
            last = await _cache.GetAsync(e.Key);
        }

        return last;
    }

    [Benchmark(Baseline = true)]
    public ValueTask<KeyValuePair<int, string?>[]> Closure() =>
        _cache.GetOrAddAsync<int>(_entries, static (s, ct) => throw new InvalidOperationException("miss"));

    [Benchmark]
    public ValueTask<KeyValuePair<int, string?>[]> Argument() =>
        _cache.GetOrAddAsync<int, int>(_entries, 7, static (a, s, ct) => throw new InvalidOperationException("miss"));
}
