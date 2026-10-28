using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>A single-key read that goes to Redis, by value size, with the read script on and off.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class RedisL2ReadBenchmark
{
    private readonly string _run = Guid.NewGuid().ToString("N");

    private IHost _host = default!;
    private ICache<string> _cache = default!;
    private string _key = default!;

    [Params(1_024, 16_384, 102_400, 524_288)]
    public int Size { get; set; }

    [Params(true, false)]
    public bool ReadScripts { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var extra = new Dictionary<string, string?>
        {
            { "Caching:Connections:Redis:ConnectionString", "127.0.0.1:6379,abortConnect=false" },
            { "Caching:Redis:ReadScriptsEnabled", ReadScripts ? "true" : "false" },
        };
        _host = HostHelper.GetHost(0, cache: KnownCacheProviderNames.Redis, extra: extra);
        await _host.StartAsync();
        _cache = _host.Services.GetRequiredService<ICache<string>>();
        _key = $"bench:l2read:{_run}:{Size}";
        await _cache.SetAsync(_key, new string('x', Size), TimeSpan.FromMinutes(30));
        if (await _cache.GetAsync(_key) is not { } v || v.Length != Size)
        {
            throw new InvalidOperationException("seed missing");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _cache.RemoveAsync(_key);
        await _host.StopAsync();
        _host.Dispose();
    }

    [Benchmark]
    public ValueTask<string?> L2Hit() => _cache.GetAsync(_key);
}
