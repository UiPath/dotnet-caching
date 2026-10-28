using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>A memory-only miss with a loader that completes at once: whether each shape completes synchronously, and its one-shot and steady cost.</summary>
internal static class MemoryMissHarness
{
    private const int Rounds = 2000;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S8969:Remove this null-forgiving operator; the compiler already knows this expression is not null here", Justification = "The operator is required: without it Task<string> does not convert to Task<string?> under the nullable compiler.")]
    public static async Task RunAsync()
    {
        var extra = new Dictionary<string, string?> { { "Caching:Connections:Redis:ConnectionString", "127.0.0.1:6379,abortConnect=false" }, { "Caching:Broadcast:RedisPubSub:Enabled", "true" } };
        static Task<string> Load() => Task.FromResult("token");

        static async Task<string> ContainsThenSet(ICache<string> cache, string key)
        {
            if (await cache.ContainsAsync(key))
            {
                return (await cache.GetAsync(key))!;
            }

            var value = await Load();
            await cache.SetAsync(key, value, DateTimeOffset.UtcNow.AddMinutes(50));
            return value;
        }

        static ValueTask<string?> WithExpiration(ICache<string> cache, string key) =>
            cache.GetOrAddWithExpirationAsync(key, Load, static async (f, _) => new GeneratedValue<string>(await f(), DateTimeOffset.UtcNow.AddMinutes(50)));

        static ValueTask<string?> GetOrAdd(ICache<string> cache, string key) =>
            cache.GetOrAddAsync(key, Load, static (f, _) => f()!);

        var shapes = new (string Name, Func<ICache<string>, string, ValueTask> Call, Func<ICache<string>, string, bool> Sync)[]
        {
            ("Contains+Set", async (c, k) => await ContainsThenSet(c, k), (c, k) => ContainsThenSet(c, k).IsCompleted),
            ("GetOrAddWithExpiration", async (c, k) => await WithExpiration(c, k), (c, k) => WithExpiration(c, k).AsTask().IsCompleted),
            ("GetOrAdd(state)", async (c, k) => await GetOrAdd(c, k), (c, k) => GetOrAdd(c, k).AsTask().IsCompleted),
        };
        var n = 0;

        var results = shapes.ToDictionary(x => x.Name, _ => (Sync: false, Steady: new List<double>(), P50: new List<double>(), P90: new List<double>()));
        for (var round = 0; round < shapes.Length; round++)
        {
            for (var s = 0; s < shapes.Length; s++)
            {
                var (name, shapeCall, shapeSync) = shapes[(s + round) % shapes.Length];

                // A fresh host per shape and round keeps the memory tier from growing across rounds.
                using var host = HostHelper.GetHost(0, cache: KnownCacheProviderNames.InMemory, topic: "RedisPubSub", extra: extra);
                await host.StartAsync();
                var cache = host.Services.GetRequiredService<ICache<string>>();
                ValueTask Call(string k) => shapeCall(cache, k);
                bool Sync(string k) => shapeSync(cache, k);
                var warmup = Stopwatch.StartNew();
                while (warmup.Elapsed < TimeSpan.FromSeconds(1))
                {
                    await Call($"warm:{name}:{n++}");
                }

                var completed = Sync($"sync:{name}:{n++}");

                var steady = Stopwatch.StartNew();
                for (var i = 0; i < Rounds; i++)
                {
                    await Call($"steady:{name}:{n++}");
                }

                var steadyUs = steady.Elapsed.TotalMicroseconds / Rounds;

                var shots = new double[50];
                for (var i = 0; i < shots.Length; i++)
                {
                    Thread.Sleep(20);
                    var one = Stopwatch.StartNew();
                    await Call($"shot:{name}:{n++}");
                    shots[i] = one.Elapsed.TotalMicroseconds;
                }

                Array.Sort(shots);
                var r = results[name];
                r.Steady.Add(steadyUs);
                r.P50.Add(shots[25]);
                r.P90.Add(shots[45]);
                results[name] = r with { Sync = completed };
                await host.StopAsync();
            }
        }

        foreach (var (name, r) in results)
        {
            Console.WriteLine($"{name,-24} sync={r.Sync,-5} steady={Median(r.Steady),7:F2} us  one-shot p50={Median(r.P50),7:F1} us  p90={Median(r.P90),7:F1} us  (median of {shapes.Length} rounds, rotated order)");
        }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
