using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Policies;
using UiPath.Caching.Redis;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class SerializerBenchmark
{
    private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(30);

    private readonly Func<CancellationToken, Task<CustomObject?>> _neverRuns = _ => throw new InvalidOperationException("A hit ran its generator");

    private CustomObject _obj = default!;
    private RedisValue _payload;
    private FakeRedisConnector _redis = default!;
    private RedisCache _cache = default!;
    private CachePolicy? _policy;

    [Params("Small", "Medium", "Large")]
    public string Size { get; set; } = "Medium";

    /// <summary>Whether a consumer serializer is registered, with a policy that names <c>json</c> so that every read looks for a format header; off is the app that never registered one.</summary>
    [Params(false, true)]
    public bool Registered { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _obj = Size switch
        {
            "Small" => CustomObject.RandomSmall(),
            "Large" => CustomObject.RandomLarge(),
            _ => CustomObject.RandomMedium(),
        };
        _payload = JsonSerializer.SerializeToUtf8Bytes(_obj);

        var registry = Registered ? new ValueSerializerRegistry([("bench", new UnusedSerializer(), false)]) : ValueSerializerRegistry.Empty;
        _policy = Registered ? new CachePolicy { Serializer = new SerializerPolicy { Write = "json" } } : null;
        _redis = new FakeRedisConnector();
        _cache = new RedisCache(
            _redis,
            new SystemJsonByteSerializerProxy(),
            EmptyResiliencePipelineProvider.Instance,
            NullTelemetryProvider.Instance,
            new RedisCacheOptions(),
            new CacheOptions { AppShortName = "bench" },
            new DefaultCachePolicyFactory(null, null, valueSerializers: registry),
            TimeProvider.System,
            NullLogger<RedisCache>.Instance)
        {
            ValueSerializers = registry,
        };
        _cache.SetAsync("bench:serializer", _obj, Expiration, _policy).AsTask().GetAwaiter().GetResult();
        if (_cache.GetAsync<CustomObject>("bench:serializer", _policy).AsTask().GetAwaiter().GetResult() is null)
        {
            throw new InvalidOperationException("The fake connector did not serve the read");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _redis.Dispose();

    [BenchmarkCategory("Serialize"), Benchmark(Baseline = true)]
    public RedisValue Serialize_String() => JsonSerializer.Serialize(_obj);

    [BenchmarkCategory("Serialize"), Benchmark]
    public RedisValue Serialize_Utf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(_obj);

    [BenchmarkCategory("Deserialize"), Benchmark(Baseline = true)]
    public CustomObject? Deserialize_String() => JsonSerializer.Deserialize<CustomObject>((string)_payload!);

    [BenchmarkCategory("Deserialize"), Benchmark]
    public CustomObject? Deserialize_Span() => JsonSerializer.Deserialize<CustomObject>(((ReadOnlyMemory<byte>)_payload).Span);

    [BenchmarkCategory("Deserialize"), Benchmark]
    public CustomObject? Deserialize_Sequence()
    {
        ReadOnlySequence<byte> payload = _payload;
        var reader = new Utf8JsonReader(payload);
        return JsonSerializer.Deserialize<CustomObject>(ref reader);
    }

    [BenchmarkCategory("RedisCacheGet"), Benchmark]
    public ValueTask<CustomObject?> RedisCache_Get() => _cache.GetAsync<CustomObject>("bench:serializer", _policy);

    [BenchmarkCategory("RedisCacheGetOrAdd"), Benchmark]
    public ValueTask<CustomObject?> RedisCache_GetOrAdd_Hit() => _cache.GetOrAddAsync<CustomObject>("bench:serializer", _neverRuns, Expiration, _policy);

    [BenchmarkCategory("RedisCacheSet"), Benchmark]
    public ValueTask<bool> RedisCache_Set() => _cache.SetAsync("bench:serializer", _obj, Expiration, _policy);

    private sealed class UnusedSerializer : ICacheValueSerializer
    {
        public IReadOnlyList<int> FormatIds { get; } = [0x10];

        public int Serialize<T>(T value, IBufferWriter<byte> target) => throw new NotSupportedException();

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) => throw new NotSupportedException();
    }
}
