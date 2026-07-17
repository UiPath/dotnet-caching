using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using StackExchange.Redis;

namespace UiPath.Caching.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class SerializerBenchmark
{
    [Params("Small", "Medium", "Large")]
    public string Size { get; set; } = "Medium";

    private CustomObject _obj = default!;
    private RedisValue _payload;

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
    }

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
}
