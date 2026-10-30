using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>Repeated <c>GetOrAddAsync</c> on one key the local tier already holds, so the generator never runs: by string, by a string composed per call, by a <c>Span&lt;char&gt;</c> composed on the stack, with a capturing lambda and with state and a static lambda, through the generator that returns its own expiration, and on a cache of a nullable value type.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class GetOrAddSameKeyBenchmark
{
    private const string KeyText = "42@acme";

    private readonly int _id = 42;
    private readonly string _tenant = "acme";

    private IHost _host = default!;
    private ICache<string> _cache = default!;
    private ICache<long?> _nullableLongCache = default!;

    [Params(KnownCacheProviderNames.InMemory, KnownCacheProviderNames.InMemoryRedis)]
    public string Provider { get; set; } = KnownCacheProviderNames.InMemory;

    [GlobalSetup]
    public async Task Setup()
    {
        _host = HostHelper.GetHost(0, cache: Provider);
        await _host.StartAsync();
        _cache = _host.Services.GetRequiredService<ICache<string>>();
        _nullableLongCache = _host.Services.GetRequiredService<ICache<long?>>();
        await _cache.GetOrAddAsync(KeyText, Generate, TimeSpan.FromHours(1));
        await _nullableLongCache.GetOrAddAsync(KeyText, GenerateNullableLong, TimeSpan.FromHours(1));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<string?> StringKey() => _cache.GetOrAddAsync(KeyText, Generate);

    [Benchmark]
    public ValueTask<string?> InterpolatedKey() => _cache.GetOrAddAsync($"{_id}@{_tenant}", Generate);

    [Benchmark]
    public ValueTask<long?> StringKeyNullableLong() => _nullableLongCache.GetOrAddAsync(KeyText, GenerateNullableLong);

    [Benchmark]
    public ValueTask<string?> StringKeyCapturingLambda()
    {
        var id = _id;
        return _cache.GetOrAddAsync(KeyText, token => Task.FromResult<string?>(id.ToString(CultureInfo.InvariantCulture)));
    }

    [Benchmark]
    public ValueTask<string?> InterpolatedKeyCapturingLambda()
    {
        var id = _id;
        return _cache.GetOrAddAsync($"{_id}@{_tenant}", token => Task.FromResult<string?>(id.ToString(CultureInfo.InvariantCulture)));
    }

    [Benchmark]
    public ValueTask<string?> StringKeyStateful() => _cache.GetOrAddAsync(KeyText, _id, static (id, token) => Task.FromResult<string?>(id.ToString(CultureInfo.InvariantCulture)));

    [Benchmark]
    public ValueTask<string?> InterpolatedKeyStateful() => _cache.GetOrAddAsync($"{_id}@{_tenant}", _id, static (id, token) => Task.FromResult<string?>(id.ToString(CultureInfo.InvariantCulture)));

    [Benchmark]
    public ValueTask<string?> WithExpirationStringKey() => _cache.GetOrAddWithExpirationAsync(KeyText, GenerateWithExpiration);

    private static Task<string?> Generate(CancellationToken token) => Task.FromResult<string?>("value");

    private static Task<long?> GenerateNullableLong(CancellationToken token) => Task.FromResult<long?>(42);

    private static Task<GeneratedValue<string>> GenerateWithExpiration(CancellationToken token) => Task.FromResult(new GeneratedValue<string>("value", DateTimeOffset.UtcNow.AddHours(1)));
}
