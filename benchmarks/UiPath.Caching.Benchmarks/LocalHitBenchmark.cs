using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>A read that hits the local tier, by key, by string, by formatted string and by span, under the default and the prefix key strategy; and the local write behind it.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[MarkdownExporterAttribute.GitHub]
public class LocalHitBenchmark
{
    private const string KeyText = "user:42";
    private static readonly CacheKey Key = KeyText;

    private readonly int _id = 42;

    private IHost _host = default!;
    private ICache<string> _cache = default!;
    private ICache<string> _prefixed = default!;

    [GlobalSetup]
    public async Task Setup()
    {
        _host = HostHelper.GetHost(0, cache: KnownCacheProviderNames.InMemory);
        await _host.StartAsync();
        _cache = _host.Services.GetRequiredService<ICache<string>>();
        _prefixed = new Cache<string>(_host.Services.GetRequiredService<ICacheFactory>().CreateCache(), new PrefixCacheKeyStrategy("app"));
        await _cache.SetAsync(Key, "value");
        await _prefixed.SetAsync(Key, "value");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Read")]
    public ValueTask<string?> ByKey() => _cache.GetAsync(Key);

    [Benchmark, BenchmarkCategory("Read")]
    public ValueTask<string?> ByString() => _cache.GetAsync(KeyText);

    [Benchmark, BenchmarkCategory("Read")]
    public ValueTask<string?> ByFormattedString() => _cache.GetAsync($"user:{_id}");

    [Benchmark, BenchmarkCategory("Read")]
    public ValueTask<string?> BySpan()
    {
        Span<char> buffer = stackalloc char[32];
        buffer.TryWrite($"user:{_id}", out var written);
        return _cache.GetAsync(buffer[..written]);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Prefixed read")]
    public ValueTask<string?> PrefixedByKey() => _prefixed.GetAsync(Key);

    [Benchmark, BenchmarkCategory("Prefixed read")]
    public ValueTask<string?> PrefixedBySpan()
    {
        Span<char> buffer = stackalloc char[32];
        buffer.TryWrite($"user:{_id}", out var written);
        return _prefixed.GetAsync(buffer[..written]);
    }

    [Benchmark, BenchmarkCategory("Write")]
    public ValueTask<bool> Set() => _cache.SetAsync(Key, "value");
}
