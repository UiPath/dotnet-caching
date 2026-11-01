using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>One host sets keys the other holds, which refreshes its local copies; an operation is one refreshed key, with a local read of each key after every <see cref="RefreshesPerRead"/> refreshes.</summary>
[ShortRunJob]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class PeerRefreshBenchmark
{
    private const int Keys = 256;
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

    private readonly string _run = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow.AddHours(1);

    private IHost _writerHost = default!;
    private IHost _readerHost = default!;
    private ICache<List<string>> _writer = default!;
    private ICache<List<string>> _reader = default!;
    private CacheKey[] _keys = default!;
    private List<string> _value = default!;
    private int _round;
    private int _firstRound;
    private int _gen1;
    private int _gen2;
    private TimeSpan _pause;

    [Params(1, 8)]
    public int RefreshesPerRead { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") is { Length: > 0 } c ? c : "127.0.0.1:6379";
        var extra = new Dictionary<string, string?>
        {
            { "Caching:Connections:Redis:ConnectionString", $"{connection},abortConnect=false" },
        };
        _writerHost = HostHelper.GetHost(0, cache: KnownCacheProviderNames.InMemoryRedis, extra: extra);
        _readerHost = HostHelper.GetHost(1, cache: KnownCacheProviderNames.InMemoryRedis, extra: extra);
        await _writerHost.StartAsync();
        await _readerHost.StartAsync();
        _writer = _writerHost.Services.GetRequiredService<ICache<List<string>>>();
        _reader = _readerHost.Services.GetRequiredService<ICache<List<string>>>();
        // About 6 KB of JSON.
        _value = [.. Enumerable.Range(0, 250).Select(i => $"value-{i:D6}-abcdefghij")];
        _keys = [.. Enumerable.Range(0, Keys).Select(i => new CacheKey($"bench:peerrefresh:{_run}:{i}"))];
        await SetAllAsync();
        foreach (var key in _keys)
        {
            if (await _reader.GetAsync(key) is not { Count: 250 })
            {
                throw new InvalidOperationException("seed missing");
            }
        }

        _firstRound = _round;
        _gen1 = GC.CollectionCount(1);
        _gen2 = GC.CollectionCount(2);
        _pause = GC.GetTotalPauseDuration();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        // Survival shows as gen1 collections; run with --noForcedGCs so the pause is not the harness's own full collections.
        var ops = (double)(_round - _firstRound) * Keys;
        var gen1 = GC.CollectionCount(1) - _gen1 - (GC.CollectionCount(2) - _gen2);
        Console.WriteLine($"// PeerRefresh: gen1-only per 1000 ops {gen1 * 1000 / ops:F3}, GC pause per 1000 ops {(GC.GetTotalPauseDuration() - _pause).TotalMilliseconds * 1000 / ops:F3} ms");
        foreach (var key in _keys)
        {
            await _writer.RemoveAsync(key);
        }

        await _writerHost.StopAsync();
        await _readerHost.StopAsync();
        _writerHost.Dispose();
        _readerHost.Dispose();
    }

    [Benchmark(OperationsPerInvoke = Keys)]
    public async Task PeerSet()
    {
        var expiration = await SetAllAsync();
        // The local copy's expiration moves once its refresh is kept; reading it does not read the value.
        foreach (var key in _keys)
        {
            while (await _reader.ExpireTimeAsync(key) is not { } at || at < expiration - Step / 2)
            {
                await Task.Yield();
            }
        }

        if (_round % RefreshesPerRead == 0)
        {
            foreach (var key in _keys)
            {
                _ = await _reader.GetAsync(key);
            }
        }
    }

    private async Task<DateTimeOffset> SetAllAsync()
    {
        var expiration = _start + (Step * ++_round);
        foreach (var key in _keys)
        {
            await _writer.SetAsync(key, _value, expiration);
        }

        return expiration;
    }
}
