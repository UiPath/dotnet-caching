using BenchmarkDotNet.Attributes;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Benchmarks;

/// <summary>Building the Redis key of a cache key through the strategy a Redis cache resolves at construction.</summary>
[MemoryDiagnoser]
public class RedisKeyBuildBenchmark
{
    private const int Pool = 1 << 12;
    private CacheKey[] _keys = default!;
    private IRedisKeyStrategy _plain = default!;
    private IRedisKeyStrategy _sharded = default!;
    private IRedisKeyStrategy _format = default!;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _keys = Enumerable.Range(0, Pool).Select(_ => new CacheKey($"tenant:{Guid.NewGuid():N}", CacheKeyCasing.Sensitive)).ToArray();
        _plain = Resolve(new CacheOptions { AppShortName = "app" });
#pragma warning disable CS0618
        _sharded = Resolve(new CacheOptions { AppShortName = "app", ShardKeyEnabled = true });
#pragma warning restore CS0618
        _format = RedisKeyFormat.Dollar.CreateStrategy("app", "se");
    }

    [Benchmark(Baseline = true)]
    public RedisKey Plain() => _plain.GetRedisKey(_keys[_next++ & (Pool - 1)]);

    [Benchmark]
    public RedisKey Sharded() => _sharded.GetRedisKey(_keys[_next++ & (Pool - 1)]);

    [Benchmark]
    public RedisKey ExplicitFormat() => _format.GetRedisKey(_keys[_next++ & (Pool - 1)]);

    private static IRedisKeyStrategy Resolve(CacheOptions options) =>
        RedisKeyStrategies.Resolve(new RedisCacheOptions(), options, typeof(RedisCache));
}
