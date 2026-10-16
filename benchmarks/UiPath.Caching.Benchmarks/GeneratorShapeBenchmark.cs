using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>One <c>GetOrAddAsync</c> with the generator in each shape a caller can write: a lambda that captures <c>this</c>, one that captures a local too, an instance delegate built once in the constructor, and a static lambda with its state; on a key the local tier holds, and on a key it never stores, so the generator runs each time.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[MarkdownExporterAttribute.GitHub]
public class GeneratorShapeBenchmark
{
    private const string HitKey = "token:42";

    // A generator that answers null stores nothing, so every read of this key runs it.
    private const string MissKey = "token:none";

    private static readonly Task<string?> Value = Task.FromResult<string?>("value");
    private static readonly Task<string?> Nothing = Task.FromResult<string?>(null);

    private readonly int _id = 42;
    private readonly Func<CancellationToken, Task<string?>> _loadBuiltOnce;
    private readonly Func<CancellationToken, Task<string?>> _loadNothingBuiltOnce;

    private IHost _host = default!;
    private ICache<string> _cache = default!;

    public GeneratorShapeBenchmark()
    {
        _loadBuiltOnce = LoadAsync;
        _loadNothingBuiltOnce = LoadNothingAsync;
    }

    [GlobalSetup]
    public async Task Setup()
    {
        _host = HostHelper.GetHost(0, cache: KnownCacheProviderNames.InMemory);
        await _host.StartAsync();
        _cache = _host.Services.GetRequiredService<ICache<string>>();
        await _cache.SetAsync(HitKey, "value", TimeSpan.FromHours(1));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Hit")]
    public ValueTask<string?> HitClosureCapturingThis() => _cache.GetOrAddAsync(HitKey, ct => LoadAsync(ct));

    [Benchmark, BenchmarkCategory("Hit")]
    public ValueTask<string?> HitClosureCapturingLocal()
    {
        var id = _id;
        return _cache.GetOrAddAsync(HitKey, ct => LoadAsync(id, ct));
    }

    [Benchmark, BenchmarkCategory("Hit")]
    public ValueTask<string?> HitDelegateBuiltOnce() => _cache.GetOrAddAsync(HitKey, _loadBuiltOnce);

    [Benchmark, BenchmarkCategory("Hit")]
    public ValueTask<string?> HitStateThis() => _cache.GetOrAddAsync(HitKey, this, static (self, ct) => self.LoadAsync(ct));

    [Benchmark, BenchmarkCategory("Hit")]
    public ValueTask<string?> HitStateTuple() => _cache.GetOrAddAsync(HitKey, (Self: this, Id: _id), static (s, ct) => s.Self.LoadAsync(s.Id, ct));

    [Benchmark(Baseline = true), BenchmarkCategory("Miss")]
    public ValueTask<string?> MissClosureCapturingThis() => _cache.GetOrAddAsync(MissKey, ct => LoadNothingAsync(ct));

    [Benchmark, BenchmarkCategory("Miss")]
    public ValueTask<string?> MissDelegateBuiltOnce() => _cache.GetOrAddAsync(MissKey, _loadNothingBuiltOnce);

    [Benchmark, BenchmarkCategory("Miss")]
    public ValueTask<string?> MissStateThis() => _cache.GetOrAddAsync(MissKey, this, static (self, ct) => self.LoadNothingAsync(ct));

    private Task<string?> LoadAsync(CancellationToken token) => Value;

    private Task<string?> LoadAsync(int id, CancellationToken token) => id == _id ? Value : Nothing;

    private Task<string?> LoadNothingAsync(CancellationToken token) => Nothing;
}
