using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace UiPath.Caching.Benchmarks;

/// <summary>Local A3 comparison: a cold GetOrAdd on InMemoryRedis with the distributed lock off, on, and on with DistributedLockSkipReread.</summary>
[MemoryDiagnoser]
public class LockedMissBenchmark
{
    private const string KeyText = "locked-cold@acme";
    private IHost _host = default!;
    private ICache<string> _cache = default!;

    /// <summary>The distributed lock: "off", "on", or "on-skip-reread" for on with DistributedLockSkipReread.</summary>
    [Params("on", "on-skip-reread", "off")]
    public string Lock { get; set; } = "on";

    [GlobalSetup]
    public async Task Setup()
    {
        _host = HostHelper.GetHost(0, cache: "InMemoryRedis", extra: new Dictionary<string, string?>
        {
            ["Caching:InMemoryRedis:DistributedLockEnabled"] = Lock == "off" ? "false" : "true",
            ["Caching:InMemoryRedis:DistributedLockSkipReread"] = Lock == "on-skip-reread" ? "true" : "false",
        });
        await _host.StartAsync();
        _cache = _host.Services.GetRequiredService<ICache<string>>();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<bool> RemoveOnly() => _cache.RemoveAsync(KeyText);

    [Benchmark]
    public async Task<string?> RemoveThenGetOrAdd()
    {
        await _cache.RemoveAsync(KeyText);
        return await _cache.GetOrAddAsync(KeyText, static _ => Task.FromResult<string?>("value"));
    }
}
