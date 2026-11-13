using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;
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

    /// <summary>Whether the default policy lists the formats it reads, which a registered serializer makes every call resolve; ignored without one.</summary>
    [Params(false, true)]
    public bool DefaultReads { get; set; }

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
        var defaultPolicy = Registered && DefaultReads ? new CachePolicy { Serializer = new SerializerPolicy { Write = "json", Read = ["json", "bench"] } } : null;
        _redis = new FakeRedisConnector();
        _cache = new RedisCache(
            _redis,
            new SystemJsonByteSerializerProxy(),
            EmptyResiliencePipelineProvider.Instance,
            NullTelemetryProvider.Instance,
            new RedisCacheOptions(),
            new CacheOptions { AppShortName = "bench" },
            new DefaultCachePolicyFactory(null, defaultPolicy, valueSerializers: registry),
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

    [BenchmarkCategory("RedisCacheGetNullPolicy"), Benchmark]
    public ValueTask<CustomObject?> RedisCache_Get_NullPolicy() => _cache.GetAsync<CustomObject>("bench:serializer", (CachePolicy?)null);

    [BenchmarkCategory("RedisCacheGetNewPolicy"), Benchmark]
    public ValueTask<CustomObject?> RedisCache_Get_NewPolicyPerCall() => _cache.GetAsync<CustomObject>("bench:serializer", new CachePolicy { DistributedExpiration = Expiration });

    [BenchmarkCategory("RedisCacheGetOrAddNewPolicy"), Benchmark]
    public ValueTask<CustomObject?> RedisCache_GetOrAdd_Hit_NewPolicyPerCall() => _cache.GetOrAddAsync<CustomObject>("bench:serializer", _neverRuns, Expiration, new CachePolicy { DistributedExpiration = Expiration });

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

/// <summary>The same cache read and write under a policy that names <c>json</c> or <c>messagepack</c>, so the rows compare size, time and allocation of the two formats through <see cref="RedisCache"/>.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class MessagePackSerializerBenchmark
{
    private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(30);

    private CustomObject _obj = default!;
    private FakeRedisConnector _redis = default!;
    private RedisCache _cache = default!;
    private ServiceProvider _services = default!;
    private CachePolicy _policy = default!;

    [Params("Small", "Medium", "Large")]
    public string Size { get; set; } = "Medium";

    [Params("json", "messagepack")]
    public string Format { get; set; } = "json";

    [GlobalSetup]
    public void Setup()
    {
        _obj = Size switch
        {
            "Small" => CustomObject.RandomSmall(),
            "Large" => CustomObject.RandomLarge(),
            _ => CustomObject.RandomMedium(),
        };
        _services = new ServiceCollection()
            .AddCaching(b => b.AddMessagePackSerializer(), o => o.AppShortName = "bench")
            .BuildServiceProvider();
        var registry = ValueSerializerRegistration.BuildRegistry(_services);
        _policy = new CachePolicy { Serializer = new SerializerPolicy { Write = Format } };
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
        _cache.SetAsync("bench:format", _obj, Expiration, _policy).AsTask().GetAwaiter().GetResult();
        var written = new ArrayBufferWriter<byte>();
        if (Format == "json")
        {
            written.Write(JsonSerializer.SerializeToUtf8Bytes(_obj));
        }
        else
        {
            registry.Entries.Single().Serializer.Serialize(_obj, written);
        }

        if (_cache.GetAsync<CustomObject>("bench:format", _policy).AsTask().GetAwaiter().GetResult() is null)
        {
            throw new InvalidOperationException("The fake connector did not serve the read");
        }

        Console.WriteLine($"{Size} {Format}: {written.WrittenCount} bytes");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _redis.Dispose();
        _services.Dispose();
    }

    [BenchmarkCategory("Get"), Benchmark]
    public ValueTask<CustomObject?> Get() => _cache.GetAsync<CustomObject>("bench:format", _policy);

    [BenchmarkCategory("Set"), Benchmark]
    public ValueTask<bool> Set() => _cache.SetAsync("bench:format", _obj, Expiration, _policy);
}

/// <summary>What a policy that migrates on read costs a read: <c>off</c> reads a value in the format the policy writes with migration off, <c>native</c> the same with it on (nothing to rewrite), <c>json</c> a value still in JSON, which every read offers and the queue refuses after the first.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class MigrateOnReadBenchmark
{
    private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(30);

    private CustomObject _obj = default!;
    private FakeRedisConnector _redis = default!;
    private RedisCache _cache = default!;
    private ValueMigration _migration = default!;
    private CachePolicy _policy = default!;
    private Func<CancellationToken, Task<CustomObject?>> _neverRuns = _ => throw new InvalidOperationException("A hit ran its generator");

    [Params("Small", "Medium")]
    public string Size { get; set; } = "Medium";

    [Params("off", "native", "json")]
    public string Mode { get; set; } = "off";

    [GlobalSetup]
    public void Setup()
    {
        _obj = Size == "Small" ? CustomObject.RandomSmall() : CustomObject.RandomMedium();
        var registry = new ValueSerializerRegistry([("bench", new JsonBodySerializer(), false)]);
        var proxy = new SystemJsonByteSerializerProxy();
        _migration = new ValueMigration(new FormatMigrator(NullTelemetryProvider.Instance, TimeProvider.System, NullLogger.Instance), new ValueRewriter(proxy, registry), registry, effective: null);
        _policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "bench", MigrateOnRead = Mode != "off" } };
        _redis = new FakeRedisConnector();
        _cache = new RedisCache(
            _redis,
            proxy,
            EmptyResiliencePipelineProvider.Instance,
            NullTelemetryProvider.Instance,
            new RedisCacheOptions(),
            new CacheOptions { AppShortName = "bench" },
            new DefaultCachePolicyFactory(null, null, valueSerializers: registry),
            TimeProvider.System,
            NullLogger<RedisCache>.Instance)
        {
            ValueSerializers = registry,
            Migration = _migration,
        };
        var seed = Mode == "json" ? new CachePolicy { Serializer = new SerializerPolicy { Write = "json" } } : _policy;
        _cache.SetAsync("bench:migrate", _obj, Expiration, seed).AsTask().GetAwaiter().GetResult();
        if (_cache.GetAsync<CustomObject>("bench:migrate", _policy).AsTask().GetAwaiter().GetResult() is null)
        {
            throw new InvalidOperationException("The fake connector did not serve the read");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _migration.Dispose();
        _redis.Dispose();
    }

    [BenchmarkCategory("Get"), Benchmark]
    public ValueTask<CustomObject?> Get() => _cache.GetAsync<CustomObject>("bench:migrate", _policy);

    [BenchmarkCategory("GetOrAdd"), Benchmark]
    public ValueTask<CustomObject?> GetOrAdd_Hit() => _cache.GetOrAddAsync<CustomObject>("bench:migrate", _neverRuns, Expiration, _policy);

    private sealed class JsonBodySerializer : ICacheValueSerializer
    {
        public IReadOnlyList<int> FormatIds { get; } = [0x10];

        public int Serialize<T>(T value, IBufferWriter<byte> target)
        {
            using var writer = new Utf8JsonWriter(target);
            JsonSerializer.Serialize(writer, value);
            return 0x10;
        }

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source)
        {
            var reader = new Utf8JsonReader(source);
            return JsonSerializer.Deserialize<T>(ref reader);
        }
    }
}

/// <summary>What a policy that migrates on read costs reading a hash of five fields, in the modes of <see cref="MigrateOnReadBenchmark"/>.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class MigrateOnReadHashBenchmark
{
    private const int Fields = 5;

    private FakeRedisConnector _redis = default!;
    private RedisHashCache _cache = default!;
    private ValueMigration _migration = default!;
    private CachePolicy _policy = default!;

    [Params("Small", "Medium")]
    public string Size { get; set; } = "Medium";

    [Params("off", "native", "json")]
    public string Mode { get; set; } = "off";

    [GlobalSetup]
    public void Setup()
    {
        var obj = Size == "Small" ? CustomObject.RandomSmall() : CustomObject.RandomMedium();
        var serializer = new JsonBodySerializer();
        var registry = new ValueSerializerRegistry([("bench", serializer, false)]);
        var proxy = new SystemJsonByteSerializerProxy();
        _migration = new ValueMigration(new FormatMigrator(NullTelemetryProvider.Instance, TimeProvider.System, NullLogger.Instance), new ValueRewriter(proxy, registry), registry, effective: null);
        _policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "bench", MigrateOnRead = Mode != "off" } };
        var stored = new ArrayBufferWriter<byte>();
        stored.Write<byte>([0x10]);
        serializer.Serialize(obj, stored);
        var value = Mode == "json" ? JsonSerializer.SerializeToUtf8Bytes(obj) : stored.WrittenSpan.ToArray();
        _redis = new FakeRedisConnector();
        _redis.SeedHash("bench:h:migrate", [.. Enumerable.Range(0, Fields).Select(i => new HashEntry($"f{i}", value))]);
        _cache = new RedisHashCache(
            _redis,
            proxy,
            EmptyResiliencePipelineProvider.Instance,
            NullTelemetryProvider.Instance,
            new RedisCacheOptions(),
            new CacheOptions { AppShortName = "bench" },
            new DefaultCachePolicyFactory(null, null, valueSerializers: registry),
            TimeProvider.System,
            NullLogger<RedisHashCache>.Instance)
        {
            ValueSerializers = registry,
            Migration = _migration,
        };
        if ((_cache.GetAsync<CustomObject>("migrate", _policy).AsTask().GetAwaiter().GetResult()).Count != Fields)
        {
            throw new InvalidOperationException("The fake connector did not serve the read");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _migration.Dispose();
        _redis.Dispose();
    }

    [BenchmarkCategory("HashGet"), Benchmark]
    public ValueTask<IDictionary<string, CustomObject?>> Get() => _cache.GetAsync<CustomObject>("migrate", _policy);

    private sealed class JsonBodySerializer : ICacheValueSerializer
    {
        public IReadOnlyList<int> FormatIds { get; } = [0x10];

        public int Serialize<T>(T value, IBufferWriter<byte> target)
        {
            using var writer = new Utf8JsonWriter(target);
            JsonSerializer.Serialize(writer, value);
            return 0x10;
        }

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source)
        {
            var reader = new Utf8JsonReader(source);
            return JsonSerializer.Deserialize<T>(ref reader);
        }
    }
}
