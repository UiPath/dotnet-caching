using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>A typed-cache local hit under a prefix key strategy, by span, by formatted string and by interpolated key.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class SpanKeyHitBenchmark
{
    private const int Sequence = 1 << 16;
    private readonly Guid _tenant = new("7f3a9c1e-22b4-4f0a-9d57-0a1c2e3f4a51");
    private Guid[] _users = default!;
    private int[] _order = default!;
    private int _next;

    private IHost _host = default!;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Callers hold ICache<T>: the interpolated keys are measured through the interface's ISpanKeyCache<T> dispatch, which the concrete type would bypass.")]
    private ICache<string> _prefixed = default!;

    [Params(1, 20_000)]
    public int Keys { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _host = HostHelper.GetHost(0, cache: KnownCacheProviderNames.InMemory);
        await _host.StartAsync();
        _prefixed = new Cache<string>(_host.Services.GetRequiredService<ICacheFactory>().CreateCache(), new PrefixCacheKeyStrategy("app"));
        _users = new Guid[Keys];
        for (var i = 0; i < Keys; i++)
        {
            _users[i] = Guid.NewGuid();
            await _prefixed.SetAsync($"{_tenant}:{_users[i]}", "value");
        }

        var random = new Random(31);
        _order = new int[Sequence];
        for (var i = 0; i < Sequence; i++)
        {
            _order[i] = random.Next(Keys);
        }

        if (await ByInterpolatedKey() != "value" || await BySpan() != "value" || await ByFormattedString() != "value" || await GetOrAddWithExpiration() != "value")
        {
            throw new InvalidOperationException("the string and interpolated keys did not resolve to the seeded value");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Benchmark]
    public ValueTask<string?> BySpan()
    {
        Span<char> buffer = stackalloc char[96];
        if (!buffer.TryWrite($"{_tenant}:{NextUser()}", out var written))
        {
            throw new InvalidOperationException("key buffer too small");
        }

        return ((ISpanKeyCache<string>)_prefixed).GetAsync(buffer[..written]);
    }

    [Benchmark]
    public ValueTask<string?> ByFormattedString()
    {
        string key = $"{_tenant}:{NextUser()}";
        return _prefixed.GetAsync(key);
    }

    [Benchmark]
    public ValueTask<string?> ByInterpolatedKey() => _prefixed.GetAsync($"{_tenant}:{NextUser()}");

    [Benchmark]
    public ValueTask<string?> GetOrAddWithExpiration() => _prefixed.GetOrAddWithExpirationAsync($"{_tenant}:{NextUser()}", static _ => throw new InvalidOperationException("miss"));

    private Guid NextUser() => _users[_order[_next++ & (Sequence - 1)]];
}
