using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using StackExchange.Redis;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Benchmarks;

/// <summary>A single-key read of a value that carries a format header: the bytes StackExchange.Redis allocates, or a leased buffer it hands back.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class ValueReadBenchmark
{
    private const int FormatId = 0x10;

    private readonly string _run = Guid.NewGuid().ToString("N");
    private readonly ValueSerializerRegistry _registry = new([("bench", new JsonBody(), false)]);

    private ConnectionMultiplexer _redis = default!;
    private IDatabase _database = default!;
    private ValueReader _reader = default!;
    private RedisKey _key;

    [Params("Small", "Medium", "Large")]
    public string Size { get; set; } = "Medium";

    [GlobalSetup]
    public async Task Setup()
    {
        _redis = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:6379,abortConnect=false");
        _database = _redis.GetDatabase();
        _reader = new ValueReader(new SystemJsonByteSerializerProxy(), _registry);
        _key = $"bench:valueread:{_run}:{Size}";
        var value = Size switch
        {
            "Small" => CustomObject.RandomSmall(),
            "Large" => CustomObject.RandomLarge(),
            _ => CustomObject.RandomMedium(),
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        var stored = new byte[body.Length + 1];
        stored[0] = FormatId;
        body.CopyTo(stored, 1);
        await _database.StringSetAsync(_key, stored, TimeSpan.FromMinutes(30));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _database.KeyDeleteAsync(_key);
        await _redis.CloseAsync();
        _redis.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task<CustomObject?> Bytes()
    {
        var value = await _database.StringGetAsync(_key);
        return _reader.Read<CustomObject>(((byte[]?)value)!).Value;
    }

    [Benchmark]
    public async Task<CustomObject?> Lease()
    {
        using var lease = await _database.StringGetLeaseAsync(_key);
        var memory = lease!.Memory;
        if (!ValueHeader.TryRead(memory.Span, out var formatId, out var headerLength) || !_registry.TryGetByFormat(formatId, out var serializer))
        {
            throw new InvalidOperationException("unexpected value");
        }

        return serializer.Deserialize<CustomObject>(formatId, new ReadOnlySequence<byte>(memory[headerLength..]));
    }

    private sealed class JsonBody : ICacheValueSerializer
    {
        public IReadOnlyList<int> FormatIds { get; } = [FormatId];

        public int Serialize<T>(T value, IBufferWriter<byte> target) => throw new NotSupportedException();

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source)
        {
            var reader = new Utf8JsonReader(source);
            return JsonSerializer.Deserialize<T>(ref reader);
        }
    }
}
