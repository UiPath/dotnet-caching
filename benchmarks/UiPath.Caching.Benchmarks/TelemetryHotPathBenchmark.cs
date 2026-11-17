using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.OpenTelemetry;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Benchmarks;

/// <summary>Redis-tier and local-tier reads and writes with the OpenTelemetry provider, a meter listener attached or not; a plain stack, not a named one.</summary>
[MemoryDiagnoser]
public class TelemetryHotPathBenchmark
{
    private static readonly CacheKey Present = "present";
    private static readonly CacheKey Absent = "absent";

    private readonly FakeRedisConnector _redis = new();
    private IHost _redisHost = default!;
    private IHost _multilayerHost = default!;
    private ICache _redisCache = default!;
    private ICache _multilayerCache = default!;
    private MeterListener? _listener;

    [Params(false, true)]
    public bool Listening { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _redisHost = await StartAsync(KnownCacheProviderNames.Redis);
        _multilayerHost = await StartAsync(KnownCacheProviderNames.InMemoryRedis);
        _redisCache = _redisHost.Services.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        _multilayerCache = _multilayerHost.Services.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis);
        await _redisCache.SetAsync(Present, "value", TimeSpan.FromMinutes(30), policy: null);
        await _multilayerCache.SetAsync(Present, "value", TimeSpan.FromMinutes(30), policy: null);

        if (Listening)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CachingTelemetryProvider.MeterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
            _listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
            _listener.Start();
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _listener?.Dispose();
        await _redisHost.StopAsync();
        await _multilayerHost.StopAsync();
        _redisHost.Dispose();
        _multilayerHost.Dispose();
    }

    [Benchmark]
    public ValueTask<string?> RedisHit() => _redisCache.GetAsync<string>(Present, policy: null);

    [Benchmark]
    public ValueTask<string?> RedisMiss() => _redisCache.GetAsync<string>(Absent, policy: null);

    [Benchmark]
    public ValueTask<bool> RedisSet() => _redisCache.SetAsync(Present, "value", TimeSpan.FromMinutes(30), policy: null);

    [Benchmark]
    public ValueTask<string?> LocalHit() => _multilayerCache.GetAsync<string>(Present, policy: null);

    [Benchmark]
    public ValueTask<string?> LocalMiss() => _multilayerCache.GetAsync<string>(Absent, policy: null);

    [Benchmark]
    public ValueTask<bool> LocalSet() => _multilayerCache.SetAsync(Present, "value", TimeSpan.FromMinutes(30), policy: null);

    private async Task<IHost> StartAsync(string cache)
    {
        var extra = new Dictionary<string, string?> { { "Caching:Connections:Redis:ConnectionString", "fake:6379,abortConnect=false" }, { "Caching:BroadcastEnabled", "false" } };
        var host = HostHelper.GetHost(0, cache: cache, topic: "RedisPubSub", extra: extra, configure: b =>
        {
            b.Services.Replace(ServiceDescriptor.Singleton<IRedisConnector>(_redis));
            b.Services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(NullLoggerFactory.Instance));
            b.Services.Replace(ServiceDescriptor.Singleton<ICachingTelemetryProvider, CachingTelemetryProvider>());
        });
        await host.StartAsync();
        return host;
    }
}
