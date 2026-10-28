using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

// A not-found-marker step (GetOrAdd miss on a fresh key, marker written, background write) measured on node 1 after an invalidation burst:
// node 2 removes `burst` keys, half of them held by node 1 and half not, which node 1 consumes from the topic stream.
// Run with:  dotnet run -c Release -- marker [endpoint] [burst 0|N] [burstSeconds] [seconds] [ratePerSec] [missShare] [held]
// Prints latency percentiles (all requests, and misses only) and the sizes of the collections reachable from node 1's cache before and after the burst.
internal static class MarkerAfterInvalidationHarness
{
    private const string Marker = "404";
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    public static async Task RunAsync(string endpoint, int burst, int burstSeconds, int seconds, int rate, double missShare, int held)
    {
        if (burst < 0 || burstSeconds < 1 || seconds < 1 || rate < 1 || rate > Stopwatch.Frequency || missShare is < 0 or > 1 || held < 1)
        {
            await Console.Error.WriteLineAsync("marker: need burst >= 0, burstSeconds >= 1, seconds >= 1, 1 <= ratePerSec <= Stopwatch.Frequency, 0 <= missShare <= 1, held >= 1");
            Environment.ExitCode = 2;
            return;
        }

        var extra = new Dictionary<string, string?>
        {
            { "Caching:Connections:Redis:ConnectionString", endpoint + ",abortConnect=false" },
            { "Caching:Broadcast:RedisStreams:AutoAck", "true" },
            { "Caching:Policies:System.String:BackgroundDistributedWrite", "true" },
        };
        using var a = HostHelper.GetHost(1, cache: KnownCacheProviderNames.InMemoryRedis, topic: "RedisStreams", extra: extra);
        using var b = HostHelper.GetHost(2, cache: KnownCacheProviderNames.InMemoryRedis, topic: "RedisStreams", extra: extra);
        await a.StartAsync();
        await b.StartAsync();
        var measured = a.Services.GetRequiredService<ICache<string>>();
        var remover = b.Services.GetRequiredService<ICache<string>>();
        var run = $"marker:{Guid.NewGuid():N}:";
        static Task<string?> Generate(CancellationToken _) => Task.FromResult<string?>(Marker);
        for (var i = 0; i < held; i++)
        {
            await measured.GetOrAddAsync(run + "held:" + i, Generate);
        }

        // Settles JIT, the first background write and the streams' first reads before anything is counted.
        await Load(measured, run + "warm:", Generate, 5, rate, missShare, run + "held:", held, record: false);
        var before = Sizes(measured);

        if (Environment.GetEnvironmentVariable("DUMP") == "1")
        {
            foreach (var (path, count) in before.OrderBy(kv => kv.Key))
            {
                Console.WriteLine($"  size {path} {count}");
            }
        }

        var burstWatch = Stopwatch.StartNew();
        var burstLoad = Task.Run(() => Load(measured, run + "during:", Generate, burstSeconds, rate, missShare, run + "held:", held, record: false));
        var perSecond = Math.Max(1, burst / burstSeconds);
        for (var i = 0; i < burst; i++)
        {
            // Half hit keys node 1 holds, half keys it never saw.
            await remover.RemoveAsync(run + (i % 2 == 0 ? "held:" + i % held : "unknown:" + i));
            if ((i + 1) % perSecond == 0)
            {
                await Task.Delay(1000);
            }
        }

        await burstLoad;
        await Task.Delay(TimeSpan.FromSeconds(3));

        var after = Sizes(measured);
        Console.WriteLine($"burst={burst} over {burstWatch.Elapsed.TotalSeconds:F0}s; collections that changed on node 1 (before -> after):");
        foreach (var (path, count) in after.Where(kv => !before.TryGetValue(kv.Key, out var c) || c != kv.Value).OrderBy(kv => kv.Key))
        {
            Console.WriteLine($"  {path} {(before.TryGetValue(path, out var was) ? was : -1)} -> {count}");
        }

        var (all, miss, errors) = await Load(measured, run + "measured:", Generate, seconds, rate, missShare, run + "held:", held, record: true);
        Console.WriteLine($"RESULT burst={burst} all p50={Ms(all, .5)} p95={Ms(all, .95)} p99={Ms(all, .99)} | miss p50={Ms(miss, .5)} p95={Ms(miss, .95)} p99={Ms(miss, .99)} n={all.Length}/{miss.Length} err={errors}");
    }

    private static async Task<(long[] All, long[] Miss, int Errors)> Load(ICache<string> cache, string freshPrefix, Func<CancellationToken, Task<string?>> generator, int seconds, int rate, double missShare, string heldPrefix, int held, bool record)
    {
        var all = new long[rate * (seconds + 5) * 11 / 10];
        var miss = new long[all.Length];
        int allCount = 0, missCount = 0, inFlight = 0, fresh = 0, errors = 0;
        var period = Stopwatch.Frequency / rate;
        var end = Stopwatch.GetTimestamp() + (long)seconds * Stopwatch.Frequency;
        var next = Stopwatch.GetTimestamp();
        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            if (now >= end)
            {
                break;
            }

            if (now < next)
            {
                Pacer.Pause(next - now);
                continue;
            }

            next += period;
            Interlocked.Increment(ref inFlight);
            var isMiss = Random.Shared.NextDouble() < missShare;
            var key = isMiss ? freshPrefix + Interlocked.Increment(ref fresh) : heldPrefix + Random.Shared.Next(held);
            ThreadPool.UnsafeQueueUserWorkItem(static s => _ = s.Run(), new Req(cache, key, generator, isMiss, record, all, miss, () => Interlocked.Increment(ref allCount) - 1, () => Interlocked.Increment(ref missCount) - 1, () => Interlocked.Decrement(ref inFlight), () => Interlocked.Increment(ref errors), next - period), preferLocal: false);
        }

        var drain = Stopwatch.StartNew();
        while (Volatile.Read(ref inFlight) > 0 && drain.Elapsed < DrainTimeout)
        {
            await Task.Delay(20);
        }

        var leftover = Volatile.Read(ref inFlight);
        if (leftover > 0)
        {
            Console.WriteLine($"INVALID: {leftover} requests did not complete within {DrainTimeout.TotalSeconds:F0}s; counted as errors");
        }

        var a = all.AsSpan(0, Math.Min(allCount, all.Length)).ToArray();
        var m = miss.AsSpan(0, Math.Min(missCount, miss.Length)).ToArray();
        Array.Sort(a);
        Array.Sort(m);
        return (a, m, Volatile.Read(ref errors) + leftover);
    }

    // Counts of every collection reachable through instance fields (breadth first, depth 14), keyed by field path; elements of small collections are followed.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security Hotspot", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "The harness walks the cache's private state on purpose.")]
    private static Dictionary<string, int> Sizes(object root)
    {
        var result = new Dictionary<string, int>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Value, string Path, int Depth)>();
        queue.Enqueue((root, root.GetType().Name, 0));
        while (queue.Count > 0)
        {
            var (value, path, depth) = queue.Dequeue();
            var type = value.GetType();
            if (value is not string && value is IEnumerable && type.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value) is int count)
            {
                result[path + ":" + type.Name] = count;
                if (count <= 64)
                {
                    var i = 0;
                    foreach (var element in (IEnumerable)value)
                    {
                        Enqueue(queue, seen, element, path + $"[{i++}]", depth);
                    }
                }

                continue;
            }

            if (depth >= 14 || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && value is not Delegate)
            {
                continue;
            }

            for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (!f.FieldType.IsValueType && f.FieldType != typeof(string))
                    {
                        Enqueue(queue, seen, f.GetValue(value), path + "." + f.Name, depth);
                    }
                }

                if (value is Delegate { Target: { } target })
                {
                    Enqueue(queue, seen, target, path + ".Target", depth);
                }
            }
        }

        return result;
    }

    private static void Enqueue(Queue<(object, string, int)> queue, HashSet<object> seen, object? child, string path, int depth)
    {
        if (child is not null && child.GetType() is { IsGenericType: true } kv && kv.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            child = kv.GetProperty("Value")!.GetValue(child);
        }

        if (child is not null and not Type and not string && !child.GetType().IsValueType && !child.GetType().IsPrimitive && seen.Add(child))
        {
            queue.Enqueue((child, path, depth + 1));
        }
    }

    private static string Ms(long[] sorted, double p) =>
        sorted.Length == 0 ? "n/a" : (sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))] * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);

    private sealed record Req(ICache<string> Cache, string Key, Func<CancellationToken, Task<string?>> Generator, bool IsMiss, bool Record, long[] All, long[] Miss, Func<int> NextAll, Func<int> NextMiss, Action Done, Action Failed, long Intended)
    {
        public async Task Run()
        {
            try
            {
                await Cache.GetOrAddAsync(Key, Generator);
                if (Record)
                {
                    var d = Stopwatch.GetTimestamp() - Intended;
                    if (NextAll() is var i && i < All.Length) { All[i] = d; }
                    if (IsMiss && NextMiss() is var j && j < Miss.Length) { Miss[j] = d; }
                }
            }
            catch (Exception)
            {
                Failed();
            }
            finally
            {
                Done();
            }
        }
    }
}
