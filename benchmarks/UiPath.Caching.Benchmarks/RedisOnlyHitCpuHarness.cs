using System.Diagnostics;
using System.Globalization;

namespace UiPath.Caching.Benchmarks;

/// <summary>RedisOnlyHitBenchmark's 256-call bursts in a plain loop, ABAB, reporting what BDN does not: process CPU per call next to wall time, thread-pool items and allocation.</summary>
internal static class RedisOnlyHitCpuHarness
{
    public static async Task RunAsync(int bursts, int rounds)
    {
        foreach (var cancellable in new[] { false, true })
        {
            var bench = new RedisOnlyHitBenchmark { Cancellable = cancellable };
            await bench.Setup();
            var shapes = new (string Name, Func<Task> Run)[] { ("GetAsync", bench.Burst256_GetAsync), ("GetOrAdd", bench.Burst256_GetOrAdd) };
            foreach (var (_, run) in shapes)
            {
                for (var i = 0; i < 300; i++)
                {
                    await run();
                }
            }

            for (var round = 0; round < rounds; round++)
            {
                foreach (var (name, run) in shapes)
                {
                    var process = Process.GetCurrentProcess();
                    var cpu0 = process.TotalProcessorTime;
                    var items0 = ThreadPool.CompletedWorkItemCount;
                    var bytes0 = GC.GetTotalAllocatedBytes(precise: true);
                    var watch = Stopwatch.StartNew();
                    for (var i = 0; i < bursts; i++)
                    {
                        await run();
                    }

                    var calls = bursts * 256.0;
                    var wall = watch.Elapsed.TotalMicroseconds / calls;
                    var bytes = (GC.GetTotalAllocatedBytes(precise: true) - bytes0) / calls;
                    var items = (ThreadPool.CompletedWorkItemCount - items0) / calls;
                    process.Refresh();
                    var cpu = (process.TotalProcessorTime - cpu0).TotalMicroseconds / calls;
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cancellable={cancellable,-5} round={round} {name,-8} wall {wall,6:F2} us/call  cpu {cpu,6:F2} us/call  items {items,5:F2}  alloc {bytes,6:F0} B/call"));
                }
            }

            await bench.Cleanup();
        }
    }
}
