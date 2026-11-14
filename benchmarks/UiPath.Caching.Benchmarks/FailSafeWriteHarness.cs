using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Benchmarks;

/// <summary>
/// Writes without fail-safe, of distinct keys from many threads, on a memory-only cache where another policy has used fail-safe: each such
/// write looks for a value kept for its key and finds none. Run with DOTNET_TieredCompilation=0 and DOTNET_gcServer=1, or allocation hides the
/// locks; prints the median writes per second of three runs.
/// </summary>
internal static class FailSafeWriteHarness
{
    private const int KeysPerThread = 4096;
    private const int Runs = 3;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public static void Run(int threads, int writes)
    {
        foreach (var sizeLimit in new long?[] { 1_000_000, null })
        {
            var rates = Enumerable.Range(0, Runs).Select(_ => Measure(sizeLimit, threads, writes)).Order().ToArray();
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"failsafe-writes sizeLimit={sizeLimit?.ToString(CultureInfo.InvariantCulture) ?? "none"} threads={threads} writes={writes} median={rates[Runs / 2] / 1e6:F2}M/s runs=[{string.Join(", ", rates.Select(r => (r / 1e6).ToString("F2", CultureInfo.InvariantCulture)))}]"));
        }
    }

    private static double Measure(long? sizeLimit, int threads, int writes)
    {
        var options = new InMemoryCacheOptions { SizeLimit = sizeLimit };
        using var cache = new MultilayerCache(
            KnownCacheProviderNames.InMemory,
            NullCache.Instance,
            new MemoryCacheFactory(TimeProvider.System, NullLoggerFactory.Instance),
            NullChangeTokenFactory.Instance,
            NullTopicFactory.Instance,
            NullCacheEventFactory.Instance,
            NullTelemetryProvider.Instance,
            options,
            options,
            new CacheOptions { AppShortName = "bench" },
            distributedLock: NullDistributedLock.Instance,
            policyFactory: NullCachePolicyFactory.Instance,
            clock: TimeProvider.System,
            logger: NullLogger.Instance);

        // From here the cache keeps values for fail-safe, so every later write looks for one.
        Write(cache, new CacheKey("kept"), new CachePolicy { FailSafeEnabled = true });
        var plain = new CachePolicy();
        var perThread = writes / threads;
        var watch = new Stopwatch();
        using var start = new Barrier(threads, _ => watch.Start());
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            var keys = Enumerable.Range(0, KeysPerThread).Select(i => new CacheKey($"t{t}:k{i}")).ToArray();
            foreach (var key in keys)
            {
                Write(cache, key, plain);
            }

            start.SignalAndWait();
            for (var i = 0; i < perThread; i++)
            {
                Write(cache, keys[i % KeysPerThread], plain);
            }
        })).ToArray();
        foreach (var worker in workers)
        {
            worker.Start();
        }

        foreach (var worker in workers)
        {
            worker.Join();
        }

        watch.Stop();
        return perThread * (double)threads / watch.Elapsed.TotalSeconds;
    }

    private static void Write(MultilayerCache cache, CacheKey key, CachePolicy policy)
    {
        var set = cache.SetAsync(key, "v", Lifetime, policy, CancellationToken.None);
        if (!(set.IsCompletedSuccessfully ? set.Result : set.AsTask().GetAwaiter().GetResult()))
        {
            throw new InvalidOperationException("The write was not kept.");
        }
    }
}
