using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>
/// Both halves of the <see cref="IDistributedCache"/> contract on the adapter, per tier and payload size.
/// The allocation columns are the point: the buffer half exists to take the per-operation array off the
/// caller's side, and the Redis write path hands a caller's memory to the wire without one in between.
/// One key, rewritten in place, so Redis holds a single entry for the run.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[MarkdownExporterAttribute.GitHub]
public class DistributedCacheBenchmark
{
    private const string Key = "bench-distributed";

    private readonly ArrayBufferWriter<byte> _destination = new();
    private readonly DistributedCacheEntryOptions _options = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) };

    private IHost _host = default!;
    private IDistributedCache _cache = default!;
    private IBufferDistributedCache _buffered = default!;
    private byte[] _payload = default!;
    private ReadOnlySequence<byte> _sequence;

    [Params(KnownCacheProviderNames.Redis, KnownCacheProviderNames.InMemory)]
    public string Tier { get; set; } = default!;

    [Params(128, 16 * 1024)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _host = HostHelper.GetHost(0, configure: b => b.AddDistributedCache(Tier));
        await _host.StartAsync();
        _cache = _host.Services.GetRequiredService<IDistributedCache>();
        _buffered = (IBufferDistributedCache)_cache;
        _payload = new byte[PayloadBytes];
        Random.Shared.NextBytes(_payload);
        _sequence = new ReadOnlySequence<byte>(_payload);
        await _cache.SetAsync(Key, _payload, _options);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _cache.RemoveAsync(Key);
        await _host.StopAsync();
        _host.Dispose();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Read")]
    public Task<byte[]?> Get() => _cache.GetAsync(Key);

    [Benchmark, BenchmarkCategory("Read")]
    public ValueTask<bool> TryGet()
    {
        _destination.ResetWrittenCount();
        return _buffered.TryGetAsync(Key, _destination);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Write")]
    public Task SetArray() => _cache.SetAsync(Key, _payload, _options);

    [Benchmark, BenchmarkCategory("Write")]
    public ValueTask SetSequence() => _buffered.SetAsync(Key, _sequence, _options);
}
