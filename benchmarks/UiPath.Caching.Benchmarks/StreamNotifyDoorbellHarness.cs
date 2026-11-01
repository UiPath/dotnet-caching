using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Benchmarks;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Benchmarks;

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
//
// The load mode writes `keys` keys round-robin at `writeHz`, so every doorbell window holds several writes and no write is
// overwritten before the reader saw it: each write is one sample. Cells are "streams:<spacing ms>" or "pubsub".
// Run with:  dotnet run -c Release --framework net8.0 -- doorbell-load [durationSec] [writeHz] [keys] [cells]
internal static class StreamNotifyDoorbellHarness
{

    private const string TimestampPrefix = "ts:";
    private static readonly string[] CountedCommands = ["psetex", "set", "xadd", "publish", "xreadgroup", "evalsha", "eval", "xack", "xinfo", "mget", "get", "pttl"];

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

    public static async Task RunLoadAsync(int durationSec, int writeHz, int keys, string cells)
    {
        Console.WriteLine($"=== Write-to-peer-visible latency under load: {keys} keys round-robin at {writeHz} Hz for {durationSec}s ===");
#pragma warning disable CA1303 // the header row is built from constants only; console output of a harness is not localized
        Console.WriteLine($"{"Cell",12} | {"Written",8} | {"Seen",8} | {"Mean",8} | {"P50",8} | {"P95",8} | {"P99",8} | {"Max",8} | {"Writer ops",10} | {"Reader ops",10} | commands (server delta)");
#pragma warning restore CA1303
        Console.WriteLine(new string('-', 150));
        foreach (var cell in cells.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (topic, spacing) = cell.StartsWith("streams:", StringComparison.Ordinal)
                ? ("RedisStreams", (TimeSpan?)TimeSpan.FromMilliseconds(double.Parse(cell["streams:".Length..], CultureInfo.InvariantCulture)))
                : (cell == "pubsub" ? "RedisPubSub" : "RedisStreams", null);
            var result = await MeasureLoadCellAsync(topic, spacing, TimeSpan.FromSeconds(durationSec), writeHz, keys);
            var sorted = result.SamplesMs.OrderBy(x => x).ToArray();
            if (sorted.Length == 0)
            {
                Console.WriteLine($"{cell,12} | {result.Written,8} | {0,8} | no samples");
                continue;
            }

            var commands = string.Join(" ", result.Commands.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"{cell,12} | {result.Written,8} | {sorted.Length,8} | {sorted.Average(),8:F2} | {sorted[sorted.Length / 2],8:F2} | {sorted[(int)(sorted.Length * 0.95)],8:F2} | {sorted[(int)(sorted.Length * 0.99)],8:F2} | {sorted[^1],8:F2} | {result.WriterOps,10} | {result.ReaderOps,10} | {commands}");
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

    private static async Task<LoadResult> MeasureLoadCellAsync(string topic, TimeSpan? spacing, TimeSpan duration, int writeHz, int keys)
    {
        Console.Error.WriteLine($"[cell] start topic={topic} spacing={spacing?.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) ?? "-"} ms");
        var overrides = new Dictionary<string, string?>
        {
            { "Caching:Broadcast:RedisStreams:MaintainerEnabled", "false" },
            { "Logging:LogLevel:Default", "Warning" },
            { "Logging:LogLevel:StackExchange.Redis", "Error" },
            { "Logging:LogLevel:Microsoft.Hosting.Lifetime", "Warning" },
        };
        if (spacing is { } s)
        {
            overrides["Caching:Broadcast:RedisStreams:NotifyMinInterval"] = s.ToString("c", CultureInfo.InvariantCulture);
        }

        if (topic == "RedisPubSub")
        {
            overrides["Caching:Broadcast:RedisPubSub:Enabled"] = "true";
        }

        using var writerHost = HostHelper.GetHost(0, topic: topic, extra: overrides);
        using var readerHost = HostHelper.GetHost(1, topic: topic, extra: overrides);
        await writerHost.StartAsync();
        await readerHost.StartAsync();
        var writerCache = writerHost.Services.GetRequiredService<ICache<string>>();
        var readerCache = readerHost.Services.GetRequiredService<ICache<string>>();
        var writerMux = writerHost.Services.GetRequiredService<IRedisConnector>().Database.Multiplexer;
        // INFO needs admin mode, which the hosts' connections do not have.
        using var admin = await ConnectionMultiplexer.ConnectAsync(writerMux.Configuration + ",allowAdmin=true");
        var server = admin.GetServers().First(s => s.IsConnected && !s.IsReplica);
        var readerMux = readerHost.Services.GetRequiredService<IRedisConnector>().Database.Multiplexer;

        var run = $"perf-doorbell-load-{Guid.NewGuid():N}:";
        var names = Enumerable.Range(0, keys).Select(i => run + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        foreach (var name in names)
        {
            await writerCache.SetAsync(name, "init");
            _ = await readerCache.GetAsync(name);
        }

        // The doorbell subscription and the consumer group come up in the background; a warm-up write shows they deliver.
        await Task.Delay(TimeSpan.FromSeconds(2));
        for (var warm = 0; warm < 3; warm++)
        {
            await writerCache.SetAsync(names[0], $"warm{warm}");
            var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 6;
            while (await readerCache.GetAsync(names[0]) != $"warm{warm}" && Stopwatch.GetTimestamp() < until)
            {
                await Task.Delay(1);
            }
        }

        var lastSeen = new string?[keys];
        for (var i = 0; i < keys; i++)
        {
            lastSeen[i] = await readerCache.GetAsync(names[i]);
        }

        var samples = new List<double>(writeHz * (int)duration.TotalSeconds + 1024);
        using var stopReader = new CancellationTokenSource();
        var readerTask = Task.Run(async () =>
        {
            while (!stopReader.IsCancellationRequested)
            {
                for (var i = 0; i < keys && !stopReader.IsCancellationRequested; i++)
                {
                    string? v;
                    try
                    {
                        v = await readerCache.GetAsync(names[i], stopReader.Token);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (v is not null && v != lastSeen[i])
                    {
                        lastSeen[i] = v;
                        if (v.StartsWith(TimestampPrefix, StringComparison.Ordinal) && long.TryParse(v.AsSpan(TimestampPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sent))
                        {
                            lock (samples)
                            {
                                samples.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
                            }
                        }
                    }
                }
            }
        });

        var before = await CommandStatsAsync(server);
        var writerOpsBefore = writerMux.OperationCount;
        var readerOpsBefore = readerMux.OperationCount;
        var interval = Stopwatch.Frequency / writeHz;
        var next = Stopwatch.GetTimestamp();
        var stopAt = next + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var written = 0;
        while (Stopwatch.GetTimestamp() < stopAt)
        {
            var remaining = next - Stopwatch.GetTimestamp();
            if (remaining > 0)
            {
                Pacer.Pause(remaining);
                continue;
            }

            await writerCache.SetAsync(names[written % keys], $"{TimestampPrefix}{Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture)}");
            written++;
            // Open loop, but a stall is not repaid as a burst.
            next = Math.Max(next + interval, Stopwatch.GetTimestamp() - interval);
        }

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var writerOps = writerMux.OperationCount - writerOpsBefore;
        var readerOps = readerMux.OperationCount - readerOpsBefore;
        var after = await CommandStatsAsync(server);
        stopReader.Cancel();
        await readerTask;
        var commands = CountedCommands.ToDictionary(c => c, c => after.GetValueOrDefault(c) - before.GetValueOrDefault(c));
        List<double> taken;
        lock (samples)
        {
            taken = [.. samples];
        }

        Console.Error.WriteLine($"[cell]   wrote={written} observed={taken.Count}");
        return new LoadResult(written, taken, writerOps, readerOps, commands);
    }

    // The server's totals, so other clients on a shared server add to the deltas.
    private static async Task<Dictionary<string, long>> CommandStatsAsync(IServer server)
    {
        var stats = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in await server.InfoAsync("commandstats"))
        {
            foreach (var (key, value) in group)
            {
                if (key.StartsWith("cmdstat_", StringComparison.Ordinal))
                {
                    stats[key["cmdstat_".Length..]] = long.Parse(value.Split(',')[0].AsSpan("calls=".Length), CultureInfo.InvariantCulture);
                }
            }
        }

        return stats;
    }

    private sealed record LoadResult(int Written, List<double> SamplesMs, long WriterOps, long ReaderOps, Dictionary<string, long> Commands);
}
