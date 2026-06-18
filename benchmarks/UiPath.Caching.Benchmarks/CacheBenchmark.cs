using BenchmarkDotNet.Attributes;
using UiPath.Caching.Benchmarks;

namespace UiPath.Caching.Benchmarks;

[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByMethod)]
[MarkdownExporterAttribute.GitHub]
[CsvExporter]
[HtmlExporter]
public class CacheBenchmark
{
    private Entry<CustomObject>[] _entries = default!;

    [Params(500, 2_500)]
    public int NumKeys { get; set; }

    [Params("Redis", "InMemoryRedis")]
    public string? Cache { get; set; }

    [Params("RedisPubSub", "RedisStreams")]
    public string? Topic { get; set; }

    [Params("Small","Medium", "Large")]
    public string? ObjectSize { get; set; }

    protected Func<CustomObject> CreateRandomObject { get; set; } = default!;

    private const int _batchSize = 50;

    [GlobalSetup]
    public void Setup()
    {
        CreateRandomObject = ObjectSize switch
        {
            "Small" => CustomObject.RandomSmall,
            "Medium" => CustomObject.RandomMedium,
            "Large" => CustomObject.RandomLarge,
            _ => throw new NotSupportedException(ObjectSize)
        };
        _entries = SetupHelper.Setup(2, Cache, $"Redis{Topic}", NumKeys, CreateRandomObject);
    }

    private ICache<CustomObject> RandomCache => _entries[Random.Shared.Next(0, _entries.Length)].Cache;


    [GlobalCleanup]
    public void Cleanup() => SetupHelper.Cleanup(_entries);

    // And define a method with the Benchmark attribute
    [Benchmark]
    public async Task ReadKnownKeys()
    {
        var tasks = Enumerable.Range(0, _batchSize).Select(async i =>
        {
            var key = $"key_{Random.Shared.Next(0, NumKeys)}";
            var tasks = Enumerable.Range(0, 4).Select(async i => await RandomCache.GetAsync(key, CancellationToken.None)).ToArray();
            await Task.WhenAll(tasks);
        });
        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task ReadUnknownKeys()
    {
        var tasks = Enumerable.Range(0, _batchSize).Select(async i =>
        {
            var key = $"key_{Guid.NewGuid()}";
            return await RandomCache.GetAsync(key, CancellationToken.None);
        });
        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task UpdateKnownKeys()
    {
        var tasks = Enumerable.Range(0, _batchSize).Select(async i =>
        {
            var key = $"key_{Random.Shared.Next(0, NumKeys)}";
            await RandomCache.SetAsync(key, CreateRandomObject(), CancellationToken.None);
            var tasks = Enumerable.Range(0, 4).Select(async i => await RandomCache.GetAsync(key, CancellationToken.None)).ToArray();
            await Task.WhenAll(tasks);
        });
        await Task.WhenAll(tasks);
    }
}
