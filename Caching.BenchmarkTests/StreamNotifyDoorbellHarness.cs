using System.Collections.Concurrent;
using System.Globalization;
using Caching.BenchmarkTests;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Caching.BenchmarkTests;

// Cache-visibility latency harness for the RedisStreamsTopic notify doorbell.
//
// Models the realistic scenario: writer pod calls cache.SetAsync(key, v); reader
// pod calls cache.GetAsync(key) in a tight loop. The reader sees the new value
// only after its L1 has been invalidated by the topic broadcast that the writer
// fires.
//
// Methodology:
//   - Two hosts (writer + reader) wired with the multilayer cache (L1 in-memory +
//     L2 Redis with broadcast invalidation).
//   - Writer does cache.SetAsync(key, $"ts:{ticks}") at `writeHz`.
//   - Reader does cache.GetAsync(key) in a tight loop. When the returned value
//     changes from the last-seen one, it records (now - ticks_in_value).
//   - Only the reader's observed latency is reported. The writer doesn't block
//     on the reader.
//
// Because the value carries the writer's UTC tick count, this works across
// processes and machines as long as clocks are NTP-synced. Locally we run two
// hosts in one process; for a true 2-machine measurement, split this into a
// `writer` and `reader` mode behind a CLI flag.
//
// Run with:  dotnet run -c Release --framework net8.0 -- doorbell [durationSec] [writeHz]
internal static class StreamNotifyDoorbellHarness
{
    public static async Task RunAsync(int durationSec = 20, int writeHz = 5)
    {
        var cells = new (bool NotifyEnabled, string PollInterval)[]
        {
            (false, "0:00:00.250"),
            (true,  "0:00:00.250"),
            (false, "0:00:01"),
            (true,  "0:00:01"),
            (false, "0:00:05"),
            (true,  "0:00:05"),
        };

        Console.WriteLine($"=== Cache-visibility latency: writer SetAsync → reader GetAsync sees new value ({durationSec}s @ {writeHz}Hz writes) ===");
        Console.WriteLine($"{"NotifyEnabled",14} | {"PollInterval",13} | {"Samples",8} | {"Mean (ms)",10} | {"P50 (ms)",10} | {"P95 (ms)",10} | {"P99 (ms)",10} | {"Max (ms)",10}");
        Console.WriteLine(new string('-', 110));

        foreach (var (notify, poll) in cells)
        {
            var samples = await MeasureCellAsync(notify, poll, TimeSpan.FromSeconds(durationSec), writeHz);
            if (samples.Count == 0)
            {
                Console.WriteLine($"{notify,14} | {poll,13} | {0,8} |   no samples");
                continue;
            }
            var sortedMs = samples.Select(t => t.TotalMilliseconds).OrderBy(x => x).ToArray();
            var mean = sortedMs.Average();
            var p50 = sortedMs[sortedMs.Length / 2];
            var p95 = sortedMs[(int)(sortedMs.Length * 0.95)];
            var p99 = sortedMs[(int)(sortedMs.Length * 0.99)];
            var max = sortedMs[^1];
            Console.WriteLine($"{notify,14} | {poll,13} | {sortedMs.Length,8} | {mean,10:F2} | {p50,10:F2} | {p95,10:F2} | {p99,10:F2} | {max,10:F2}");
        }
    }

    private static async Task<List<TimeSpan>> MeasureCellAsync(bool notifyEnabled, string pollInterval, TimeSpan duration, int writeHz)
    {
        Console.Error.WriteLine($"[cell] start NotifyEnabled={notifyEnabled} PollInterval={pollInterval}");
        var overrides = new Dictionary<string, string?>
        {
            { "Caching:Broadcast:RedisStreams:NotifyEnabled", notifyEnabled.ToString() },
            { "Caching:Broadcast:RedisStreams:PollInterval", pollInterval },
            { "Caching:Broadcast:RedisStreams:MaintainerEnabled", "false" },
            { "Logging:LogLevel:Default", "Warning" },
            { "Logging:LogLevel:StackExchange.Redis", "Error" },
            { "Logging:LogLevel:Microsoft.Hosting.Lifetime", "Warning" },
        };
        using var writerHost = HostHelper.GetHost(0, topic: "RedisStreams", extra: overrides);
        using var readerHost = HostHelper.GetHost(1, topic: "RedisStreams", extra: overrides);
        await writerHost.StartAsync();
        await readerHost.StartAsync();

        var connector = writerHost.Services.GetRequiredService<IRedisConnector>();
        connector.Database.Execute("FLUSHALL", args: Array.Empty<object>(), flags: CommandFlags.DemandMaster);

        var writerCache = writerHost.Services.GetRequiredService<ICache<string>>();
        var readerCache = readerHost.Services.GetRequiredService<ICache<string>>();

        var key = $"perf-doorbell-{Guid.NewGuid():N}";

        try
        {
            // Initialize the key so the stream / consumer group / notify subscription
            // are all warm before we start measuring.
            await writerCache.SetAsync(key, "init");
            // Prime the reader's L1 with the initial value.
            _ = await readerCache.GetAsync(key);

            var pollSpan = TimeSpan.Parse(pollInterval, CultureInfo.InvariantCulture);
            await Task.Delay(TimeSpan.FromSeconds(2) + pollSpan);

            // Force a couple of warm-up writes to make sure the broadcast pipeline is
            // delivering before we start timing.
            for (var i = 0; i < 3; i++)
            {
                await writerCache.SetAsync(key, $"warmup_{i}");
                await Task.Delay(pollSpan + TimeSpan.FromMilliseconds(200));
                _ = await readerCache.GetAsync(key);
            }

            // Reset reader's last-seen so the timed window starts clean.
            var lastSeenValue = await readerCache.GetAsync(key);

            var samples = new ConcurrentBag<TimeSpan>();
            var stopReader = new CancellationTokenSource();

            // Reader: tight GetAsync loop. Records (now - ts) whenever the value
            // changes to a "ts:..." payload from the timed-window writes below.
            var readerTask = Task.Run(async () =>
            {
                while (!stopReader.IsCancellationRequested)
                {
                    try
                    {
                        var v = await readerCache.GetAsync(key, stopReader.Token);
                        var observedAt = DateTime.UtcNow;
                        if (v is not null
                            && !ReferenceEquals(v, lastSeenValue)
                            && v != lastSeenValue
                            && v.StartsWith(TimestampPrefix, StringComparison.Ordinal)
                            && long.TryParse(v.AsSpan(TimestampPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sentTicks))
                        {
                            var elapsed = observedAt - new DateTime(sentTicks, DateTimeKind.Utc);
                            if (elapsed >= TimeSpan.Zero)
                            {
                                samples.Add(elapsed);
                            }
                            lastSeenValue = v;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception) { /* swallow transient failures; reader keeps going */ }
                }
            });

            // Writer: update key with timestamp-bearing values at writeHz.
            var stopAt = DateTime.UtcNow + duration;
            var writeInterval = TimeSpan.FromSeconds(1.0 / writeHz);
            var written = 0;
            while (DateTime.UtcNow < stopAt)
            {
                await writerCache.SetAsync(key, $"{TimestampPrefix}{DateTime.UtcNow.Ticks}");
                written++;
                await Task.Delay(writeInterval);
            }

            // Drain: give the reader at least one full PollInterval (no doorbell case)
            // plus a small buffer to observe the last write.
            await Task.Delay(pollSpan + TimeSpan.FromSeconds(1));
            stopReader.Cancel();
            await readerTask;

            Console.Error.WriteLine($"[cell]   wrote={written} observed={samples.Count}");
            return samples.ToList();
        }
        finally
        {
            // Hosts dispose via 'using'.
        }
    }

    private const string TimestampPrefix = "ts:";
}
