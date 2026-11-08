using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Benchmarks;

/// <summary>Hits on a Redis-only cache against a fake connector: GetAsync against GetOrAddAsync, one call and 256 concurrent calls on distinct keys, and 32 concurrent misses on one key.</summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class RedisOnlyHitBenchmark
{
    private const int Burst = 256;
    private static readonly List<string> Value = ["Folders.View", "Folders.Edit", "Jobs.View", "Jobs.Create", "Queues.View"];
    private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(30);
    private static long _hitRuns;

    private readonly FakeRedisConnector _redis = new();
    private readonly CancellationTokenSource _cts = new();
    private IHost _host = default!;
    private ICache _cache = default!;
    private CacheKey[] _keys = default!;
    private CacheKey _missKey;
    private ValueTask<List<string>?>[] _pending = default!;
    private TaskCompletionSource _gate = default!;
    private long _missRuns;

    [Params(false, true)]
    public bool Cancellable { get; set; }

    private CancellationToken Token => Cancellable ? _cts.Token : default;

    [GlobalSetup]
    public async Task Setup()
    {
        var extra = new Dictionary<string, string?> { { "Caching:Connections:Redis:ConnectionString", "fake:6379,abortConnect=false" }, { "Caching:Broadcast:RedisPubSub:Enabled", "true" } };
        _host = HostHelper.GetHost(0, cache: KnownCacheProviderNames.Redis, topic: "RedisPubSub", extra: extra, configure: b => b.Services.Replace(ServiceDescriptor.Singleton<IRedisConnector>(_redis)));
        await _host.StartAsync();
        _cache = _host.Services.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var user = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        _keys = [.. Enumerable.Range(0, Burst).Select(_ => new CacheKey($"{Guid.NewGuid()}_{user}_{tenant}"))];
        _missKey = new CacheKey($"miss_{user}_{tenant}");
        _pending = new ValueTask<List<string>?>[Burst];
        foreach (var key in _keys)
        {
            await _cache.SetAsync(key, Value, Expiration, policy: null);
        }

        if ((await _cache.GetAsync<List<string>>(_keys[0], policy: null))?.Count != Value.Count || _redis.Gets == 0)
        {
            throw new InvalidOperationException("The fake connector did not serve the read");
        }

        if (await _cache.GetOrAddAsync(_keys[0], 1, HitGenerator, Expiration, policy: null) is null || Volatile.Read(ref _hitRuns) != 0)
        {
            throw new InvalidOperationException("A hit ran its generator");
        }

        await MissBurstAsync(default);
        await MissBurstAsync(default);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _host.StopAsync();
        _host.Dispose();
        _redis.Dispose();
        _cts.Dispose();
        if (Volatile.Read(ref _hitRuns) != 0)
        {
            throw new InvalidOperationException("A hit ran its generator");
        }
    }

    [Benchmark]
    public ValueTask<List<string>?> Single_GetAsync() => _cache.GetAsync<List<string>>(_keys[0], policy: null, Token);

    [Benchmark]
    public ValueTask<List<string>?> Single_GetOrAdd() => _cache.GetOrAddAsync(_keys[0], 1, HitGenerator, Expiration, policy: null, Token);

    [Benchmark(OperationsPerInvoke = Burst)]
    public async Task Burst256_GetAsync()
    {
        var token = Token;
        for (var i = 0; i < Burst; i++)
        {
#pragma warning disable CA2012 // the pending values are awaited after the whole burst is issued, which is the shape measured
            _pending[i] = _cache.GetAsync<List<string>>(_keys[i], policy: null, token);
#pragma warning restore CA2012
        }

        for (var i = 0; i < Burst; i++)
        {
            await _pending[i];
        }
    }

    [Benchmark(OperationsPerInvoke = Burst)]
    public async Task Burst256_GetOrAdd()
    {
        var token = Token;
        for (var i = 0; i < Burst; i++)
        {
#pragma warning disable CA2012 // the pending values are awaited after the whole burst is issued, which is the shape measured
            _pending[i] = _cache.GetOrAddAsync(_keys[i], 1, HitGenerator, Expiration, policy: null, token);
#pragma warning restore CA2012
        }

        for (var i = 0; i < Burst; i++)
        {
            await _pending[i];
        }
    }

    /// <summary>32 callers on one key Redis does not hold: one read and one generator run between them while the single flight holds.</summary>
    [Benchmark(OperationsPerInvoke = 32)]
    public Task ConcurrentMiss32_GetOrAdd() => MissBurstAsync(Token);

    private static Task<List<string>?> HitGenerator(int state, CancellationToken token)
    {
        Interlocked.Increment(ref _hitRuns);
        return Task.FromResult<List<string>?>(Value);
    }

    private static async Task<List<string>?> MissGenerator(RedisOnlyHitBenchmark self, CancellationToken token)
    {
        Interlocked.Increment(ref self._missRuns);
        await self._gate.Task;
        return Value;
    }

    private async Task MissBurstAsync(CancellationToken token)
    {
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = Volatile.Read(ref _missRuns);
        for (var i = 0; i < 32; i++)
        {
#pragma warning disable CA2012 // the pending values are awaited after the whole burst is issued, which is the shape measured
            _pending[i] = _cache.GetOrAddAsync(_missKey, this, MissGenerator, Expiration, policy: null, token);
#pragma warning restore CA2012
        }

        _gate.SetResult();
        for (var i = 0; i < 32; i++)
        {
            await _pending[i];
        }

        if (Volatile.Read(ref _missRuns) - runs != 1)
        {
            throw new InvalidOperationException("The 32 misses did not share one generator run");
        }

        _redis.ForgetLastWrite();
    }
}
