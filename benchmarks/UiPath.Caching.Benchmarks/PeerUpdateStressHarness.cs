using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>Two nodes with peer field updates hammer the same hashes with one-field HashReplace writes and reads; reports per-node throughput each second and any worker that goes without completing a call for `stallSeconds`.</summary>
internal static class PeerUpdateStressHarness
{
    public static async Task RunAsync(string endpoint, int seconds, int keys, int threads, double stallSeconds = 3)
    {
        var extra = new Dictionary<string, string?>
        {
            { "Caching:Connections:Redis:ConnectionString", endpoint + ",abortConnect=false" },
            { "Caching:InMemoryRedis:PeerFieldUpdates", Environment.GetEnvironmentVariable("PEER") ?? "true" },
        };
        using var a = HostHelper.GetHost(1, cache: KnownCacheProviderNames.InMemoryRedis, topic: "RedisStreams", extra: extra);
        using var b = HostHelper.GetHost(2, cache: KnownCacheProviderNames.InMemoryRedis, topic: "RedisStreams", extra: extra);
        await a.StartAsync();
        await b.StartAsync();
        var caches = new[] { a.Services.GetRequiredService<IHashCache<string>>(), b.Services.GetRequiredService<IHashCache<string>>() };
        var run = $"stress:{Guid.NewGuid():N}:";
        var options = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(30), null, null, HashCacheSetOption.HashReplace);
        var ops = new long[2];
        var errors = new long[2];
        var stallTicks = (long)(stallSeconds * Stopwatch.Frequency);
        var lastDone = new long[threads * 2];
        var maxStall = new long[2];
        Array.Fill(lastDone, Stopwatch.GetTimestamp());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        void NoteGap(int node, long gap)
        {
            long seen;
            while (gap > (seen = Interlocked.Read(ref maxStall[node])) && Interlocked.CompareExchange(ref maxStall[node], gap, seen) != seen)
            {
            }
        }

        var workers = Enumerable.Range(0, threads * 2).Select(w => Task.Run(async () =>
        {
            var node = w % 2;
            var cache = caches[node];
            while (!stop.IsCancellationRequested)
            {
                var key = run + Random.Shared.Next(keys);
                try
                {
                    var fields = await cache.GetAsync(key);
                    if (fields.Count == 0 || Random.Shared.Next(4) == 0)
                    {
                        await cache.SetAsync(key, new Dictionary<string, string?> { [$"ip:{Random.Shared.Next(250)}"] = "1" }, options);
                    }

                    Interlocked.Increment(ref ops[node]);
                }
                catch (Exception ex)
                {
                    if (Interlocked.Increment(ref errors[node]) <= 3)
                    {
                        Console.WriteLine($"error: {ex.GetType().Name}: {ex.Message[..Math.Min(160, ex.Message.Length)]}");
                    }
                }

                var done = Stopwatch.GetTimestamp();
                NoteGap(node, done - Interlocked.Exchange(ref lastDone[w], done));
            }
        })).ToArray();

        var watch = Stopwatch.StartNew();
        long[] lastOps = [0, 0];
        var lastTick = watch.Elapsed;
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(1000);
            var elapsed = watch.Elapsed - lastTick;
            lastTick = watch.Elapsed;
            var now = Stopwatch.GetTimestamp();
            var nowOps = new[] { Interlocked.Read(ref ops[0]), Interlocked.Read(ref ops[1]) };
            var stalled = new int[2];
            for (var w = 0; w < lastDone.Length; w++)
            {
                var age = now - Interlocked.Read(ref lastDone[w]);
                if (!workers[w].IsCompleted && age > stallTicks)
                {
                    stalled[w % 2]++;
                    NoteGap(w % 2, age);
                }
            }

            var stall = stalled[0] + stalled[1] > 0 ? $" STALLED workers a={stalled[0]} b={stalled[1]} (>{stallSeconds:F0}s)" : string.Empty;
            Console.WriteLine($"t={watch.Elapsed.TotalSeconds,4:F0}s ops/s a={(nowOps[0] - lastOps[0]) / elapsed.TotalSeconds,7:F0} b={(nowOps[1] - lastOps[1]) / elapsed.TotalSeconds,7:F0} errors a={Interlocked.Read(ref errors[0])} b={Interlocked.Read(ref errors[1])}{stall} pid={Environment.ProcessId}");
            lastOps = nowOps;
        }

        await Task.WhenAny(Task.WhenAll(workers), Task.Delay(TimeSpan.FromSeconds(15)));
        var stuckA = workers.Where((t, i) => i % 2 == 0 && !t.IsCompleted).Count();
        var stuckB = workers.Where((t, i) => i % 2 == 1 && !t.IsCompleted).Count();
        var stuck = stuckA + stuckB;
        var endNow = Stopwatch.GetTimestamp();
        for (var w = 0; w < lastDone.Length; w++)
        {
            if (!workers[w].IsCompleted)
            {
                NoteGap(w % 2, endNow - Interlocked.Read(ref lastDone[w]));
            }
        }

        var stallReport = Enumerable.Range(0, 2).Where(n => Interlocked.Read(ref maxStall[n]) > stallTicks).Select(n => $"node {(n == 0 ? "a" : "b")} max {Interlocked.Read(ref maxStall[n]) / (double)Stopwatch.Frequency:F1}s").ToArray();
        Console.WriteLine(stallReport.Length == 0 ? $"STALL: none (threshold {stallSeconds:F0}s)" : $"STALL: {string.Join("; ", stallReport)} (threshold {stallSeconds:F0}s)");
        var report = stuck > 0 ? $"STUCK: {stuck} workers did not finish (node a={stuckA}, node b={stuckB}) pid={Environment.ProcessId}" : "workers finished";
        Console.WriteLine(report);
        if (stuck > 0 && Environment.GetEnvironmentVariable("HOLD") is { } hold)
        {
            await Task.Delay(TimeSpan.FromSeconds(int.Parse(hold, CultureInfo.InvariantCulture)));
        }
    }
}
