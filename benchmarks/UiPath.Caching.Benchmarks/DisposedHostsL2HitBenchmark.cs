using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>A multilayer L2 hit after N hosts with Redis Streams and the notify doorbell were built and disposed: a disposed host must leave no timers behind.</summary>
[ShortRunJob, InvocationCount(1, 1), IterationCount(30), WarmupCount(3)]
[ThreadingDiagnoser]
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class DisposedHostsL2HitBenchmark
{
    private readonly string _run = Guid.NewGuid().ToString("N");

    private IHost? _host;
    private ICache<string>? _cache;
    private string _key = default!;

    [Params(0, 200)]
    public int DisposedHosts { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection))
        {
            return;
        }
        var extra = new Dictionary<string, string?>
        {
            { "Caching:Connections:Redis:ConnectionString", $"{connection},abortConnect=false" },
            { "Caching:Broadcast:RedisStreams:NotifyEnabled", "true" },
            { "Caching:InMemoryRedis:LocalMaxExpiration", "00:00:00.001" },
            { "Caching:InMemoryRedis:LocalMaxExpirationDisconnected", "00:00:00.001" },
        };
        for (var i = 0; i < DisposedHosts; i++)
        {
            using var host = HostHelper.GetHost(i, cache: KnownCacheProviderNames.InMemoryRedis, extra: extra);
            await host.StartAsync();
            // Ask for the cache so its topic exists; the notify timer is set from there.
            _ = host.Services.GetRequiredService<ICache<string>>();
            await host.StopAsync();
        }

        _host = HostHelper.GetHost(DisposedHosts, cache: KnownCacheProviderNames.InMemoryRedis, extra: extra);
        await _host.StartAsync();
        _cache = _host.Services.GetRequiredService<ICache<string>>();
        _key = $"bench:disposed:{_run}";
        await _cache.SetAsync(_key, new string('x', 1_024), TimeSpan.FromMinutes(30));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_host is null)
        {
            return;
        }
        await _cache!.RemoveAsync(_key);
        await _host.StopAsync();
        _host.Dispose();
    }

    [IterationSetup]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "BenchmarkDotNet runs iteration setup on the benchmark instance.")]
    public void WaitForLocalExpiry() => Thread.Sleep(5);

    [Benchmark]
    public ValueTask<string?> L2Hit() => _cache is null ? default : _cache.GetAsync(_key);
}
