using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using StackExchange.Redis;

namespace UiPath.Caching.Benchmarks;

/// <summary>The parts of a single-flight hit, without any read: key hashing, the table, and a whole run.</summary>
[MemoryDiagnoser]
public class InFlightCostBenchmark
{
    private static readonly List<string> Value = ["a"];
    private readonly ConcurrentDictionary<RedisKey, object> _table = new();
    private readonly InFlight<RedisKey, List<string>?> _flights = new();
    private readonly object _entry = new();
    private RedisKey _key;
    private RedisKey _copy;

    [GlobalSetup]
    public void Setup()
    {
        var name = $"{Guid.NewGuid()}_{Guid.NewGuid()}_{Guid.NewGuid()}";
        _key = ((RedisKey)name).Prepend("UiPath.Caching:List`1:");
        _copy = ((RedisKey)new string(name.AsSpan())).Prepend("UiPath.Caching:List`1:");
    }

    [Benchmark]
    public int KeyHash() => _key.GetHashCode();

    [Benchmark]
    public bool KeyEquals() => _key == _copy;

    [Benchmark]
    public bool TableAddRemove() => _table.TryAdd(_key, _entry) && _table.TryRemove(new KeyValuePair<RedisKey, object>(_key, _entry));

    /// <summary>A run whose work yields once and is then completed by hand, so the cost is the table and the run, not a hop.</summary>
    [Benchmark]
    public List<string>? RunYieldingOnce()
    {
        var source = new TaskCompletionSource<List<string>?>();
#pragma warning disable CA2012 // the result is read once the source is completed; awaiting would change what is measured
        var pending = _flights.StartOrJoinAsync(
            _key,
            source,
            static async (s, run) =>
            {
                var v = await s.Task.ConfigureAwait(false);
                run.Complete(v);
                return v;
            },
            default,
            out _);
        source.SetResult(Value);
        return pending.Result;
#pragma warning restore CA2012
    }

    [Benchmark(Baseline = true)]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "BenchmarkDotNet runs instance methods.")]
    public List<string>? NoFlightYieldingOnce()
    {
        var source = new TaskCompletionSource<List<string>?>();
#pragma warning disable CA2012 // the result is read once the source is completed; awaiting would change what is measured
        var pending = Work(source);
        source.SetResult(Value);
        return pending.Result;
#pragma warning restore CA2012

        static async ValueTask<List<string>?> Work(TaskCompletionSource<List<string>?> s) => await s.Task.ConfigureAwait(false);
    }
}
