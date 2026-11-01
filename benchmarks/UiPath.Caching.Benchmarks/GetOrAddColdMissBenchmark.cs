using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>One caller, one key the cache does not hold: <c>GetOrAddAsync</c> after a <c>RemoveAsync</c>, so every call runs the generator and writes; the state forms are the closure-free overloads. <c>RemoveOnly</c> is what the removal costs.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class GetOrAddColdMissBenchmark
{
    private const string KeyText = "cold@acme";

    private IHost _host = default!;
    private ICache<string> _cache = default!;

    [Params(KnownCacheProviderNames.InMemory, KnownCacheProviderNames.InMemoryRedis)]
    public string Provider { get; set; } = KnownCacheProviderNames.InMemory;

    [GlobalSetup]
    public async Task Setup()
    {
        _host = HostHelper.GetHost(0, cache: Provider);
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
        return await _cache.GetOrAddAsync(KeyText, Generate);
    }

    [Benchmark]
    public async Task<string?> RemoveThenGetOrAddWithExpiration()
    {
        await _cache.RemoveAsync(KeyText);
        return await _cache.GetOrAddWithExpirationAsync(KeyText, GenerateWithExpiration);
    }

    [Benchmark]
    public async Task<string?> RemoveThenGetOrAddState()
    {
        await _cache.RemoveAsync(KeyText);
        return await _cache.GetOrAddAsync(KeyText, "value", GenerateFromState);
    }

    [Benchmark]
    public async Task<string?> RemoveThenGetOrAddWithExpirationState()
    {
        await _cache.RemoveAsync(KeyText);
        return await _cache.GetOrAddWithExpirationAsync(KeyText, "value", GenerateWithExpirationFromState);
    }

    private static Task<string?> Generate(CancellationToken token) => Task.FromResult<string?>("value");

    private static Task<GeneratedValue<string>> GenerateWithExpiration(CancellationToken token) => Task.FromResult(new GeneratedValue<string>("value", DateTimeOffset.UtcNow.AddHours(1)));

    private static Task<string?> GenerateFromState(string state, CancellationToken token) => Task.FromResult<string?>(state);

    private static Task<GeneratedValue<string>> GenerateWithExpirationFromState(string state, CancellationToken token) => Task.FromResult(new GeneratedValue<string>(state, DateTimeOffset.UtcNow.AddHours(1)));
}
