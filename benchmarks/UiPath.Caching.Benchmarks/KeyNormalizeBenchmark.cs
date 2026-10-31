using BenchmarkDotNet.Attributes;

namespace UiPath.Caching.Benchmarks;

/// <summary>Building a <see cref="CacheKey"/> from random lowercase GUID-hex text, which normalizes it.</summary>
[MemoryDiagnoser]
public class KeyNormalizeBenchmark
{
    private const int Pool = 1 << 12;
    private string[] _texts = default!;
    private int _next;

    [Params(73, 170)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _texts = new string[Pool];
        for (var i = 0; i < Pool; i++)
        {
            var text = string.Concat(Enumerable.Range(0, (Length / 32) + 1).Select(_ => Guid.NewGuid().ToString("N")));
            _texts[i] = text[..Length];
        }
    }

    [Benchmark]
    public CacheKey Normalize() => new(_texts[_next++ & (Pool - 1)].AsSpan());
}
