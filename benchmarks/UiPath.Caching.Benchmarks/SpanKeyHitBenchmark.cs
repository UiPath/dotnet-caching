using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>A typed-cache local hit under a prefix key strategy, by span and by string.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class SpanKeyHitBenchmark
{
    private readonly int _id = 42;

    private IHost _host = default!;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Callers hold ICache<T>: the span overloads are measured through the interface's ISpanKeyCache<T> dispatch, which the concrete type would bypass.")]
    private ICache<string> _prefixed = default!;

    [GlobalSetup]
    public async Task Setup()
    {
        _host = HostHelper.GetHost(0, cache: KnownCacheProviderNames.InMemory);
        await _host.StartAsync();
        _prefixed = new Cache<string>(_host.Services.GetRequiredService<ICacheFactory>().CreateCache(), new PrefixCacheKeyStrategy("app"));
        await _prefixed.SetAsync("tenant:7f3a9c1e-22b4:user:42", "value");
        if (await ByString() != "value" || await BySpan() != "value" || await GetOrAddWithExpirationBySpan() != "value")
        {
            throw new InvalidOperationException("the string and span keys did not resolve to the seeded value");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Benchmark]
    public ValueTask<string?> ByString() => _prefixed.GetAsync($"tenant:7f3a9c1e-22b4:user:{_id}");

    [Benchmark]
    public ValueTask<string?> BySpan()
    {
        Span<char> buffer = stackalloc char[32];
        if (!buffer.TryWrite($"tenant:7f3a9c1e-22b4:user:{_id}", out var written))
        {
            throw new InvalidOperationException("key buffer too small");
        }

        return _prefixed.GetAsync(buffer[..written]);
    }

    [Benchmark]
    public ValueTask<string?> GetOrAddWithExpirationBySpan()
    {
        Span<char> buffer = stackalloc char[32];
        if (!buffer.TryWrite($"tenant:7f3a9c1e-22b4:user:{_id}", out var written))
        {
            throw new InvalidOperationException("key buffer too small");
        }

        return _prefixed.GetOrAddWithExpirationAsync(buffer[..written], static _ => throw new InvalidOperationException("miss"));
    }
}
