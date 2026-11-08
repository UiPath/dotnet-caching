using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Benchmarks;

/// <summary>What the Streams doorbell adds per broadcast write: 8 concurrent sets on an InMemoryRedis cache per op, as Metadata_Miss writes its 8 hashes, with NotifyEnabled false against true, ABAB in one process. Counts thread-pool items, lock contentions and process CPU per op. Keys live under a fresh AppShortName and are deleted afterwards; nothing is flushed.</summary>
internal static class DoorbellCostHarness
{
    private const int Writes = 8;

    private static bool CountingDoorbells => Environment.GetEnvironmentVariable("COUNT_DOORBELLS") == "1";

    public static async Task RunAsync(int ops, int rounds, string cells)
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? throw new InvalidOperationException("REDIS_CONNECTION_STRING is not set");
        var app = $"rca311{Guid.NewGuid():N}"[..14];
        var value = new string('x', 6600);
        var counting = CountingDoorbells;
        Console.WriteLine($"app prefix {app}; {Writes} sets per op, {ops} ops per cell; doorbell counting {(counting ? "on (a subscriber perturbs items/op and cpu)" : "off")}");
#pragma warning disable CA1303 // the header row is built from constants only; console output of a harness is not localized
        Console.WriteLine("cell        round  wall_ms/op  cpu_us/op  items/op  locks/op");
#pragma warning restore CA1303
        var totals = new Dictionary<string, List<(double Wall, double Cpu, double Items, double Locks)>>();
        for (var round = 0; round < rounds; round++)
        {
            foreach (var cell in cells.Split(','))
            {
                var r = await MeasureAsync(connection, app, cell, ops, value);
                (totals.TryGetValue(cell, out var l) ? l : totals[cell] = []).Add(r);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{cell,-11} {round,5}  {r.Wall,10:F2}  {r.Cpu,9:F0}  {r.Items,8:F2}  {r.Locks,8:F3}"));
            }
        }

        foreach (var (cell, l) in totals)
        {
            static double Median(IEnumerable<double> xs)
            {
                var s = xs.Order().ToArray();
                return s.Length % 2 == 1 ? s[s.Length / 2] : (s[(s.Length / 2) - 1] + s[s.Length / 2]) / 2;
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"median {cell,-11} wall {Median(l.Select(x => x.Wall)),8:F2} ms  cpu {Median(l.Select(x => x.Cpu)),7:F0} us  items {Median(l.Select(x => x.Items)),6:F2}  locks {Median(l.Select(x => x.Locks)),6:F3}"));
        }

        await CleanupAsync(connection, app);
    }

    /// <summary>Deletes what runs under <paramref name="prefix"/> left behind, for a run that ended before its own cleanup.</summary>
    public static Task CleanupAsync(string prefix) =>
        CleanupAsync(Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? throw new InvalidOperationException("REDIS_CONNECTION_STRING is not set"), prefix);

    private static async Task<(double Wall, double Cpu, double Items, double Locks)> MeasureAsync(string connection, string app, string cell, int ops, string value)
    {
        // nonotify, notify, or notify:<ms> for a NotifyMinInterval between doorbells.
        var notify = cell.StartsWith("notify", StringComparison.Ordinal) ? "true" : cell == "nonotify" ? "false" : throw new ArgumentException(cell);
        var spacing = cell.StartsWith("notify:", StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(int.Parse(cell["notify:".Length..], CultureInfo.InvariantCulture)).ToString() : null;
        var extra = new Dictionary<string, string?>
        {
            { "Caching:AppShortName", app },
            { "Caching:Connections:Redis:ConnectionString", connection },
            { "Caching:Broadcast:RedisStreams:NotifyEnabled", notify },
            { "Caching:Broadcast:RedisStreams:Enabled", "true" },
            { "Caching:Broadcast:RedisStreams:PollBatchSize", "512" },
            { "Logging:LogLevel:Default", "Warning" },
            { "Logging:LogLevel:StackExchange.Redis", "Error" },
        };
        if (spacing is not null)
        {
            extra["Caching:Broadcast:RedisStreams:NotifyMinInterval"] = spacing;
        }
        using var host = HostHelper.GetHost(0, cache: KnownCacheProviderNames.InMemoryRedis, topic: "RedisStreams", extra: extra);
        await host.StartAsync();
        var cache = host.Services.GetRequiredService<ICache<string>>();
        var run = Guid.NewGuid().ToString("N")[..8];
        var tasks = new Task[Writes];
        async Task Op(int i)
        {
            for (var w = 0; w < Writes; w++)
            {
                tasks[w] = cache.SetAsync($"{run}:{i}:{w}", value, TimeSpan.FromMinutes(5)).AsTask();
            }

            await Task.WhenAll(tasks);
        }

        // Warm: the stream, the group, the subscription and the JIT.
        for (var i = 0; i < 30; i++)
        {
            await Op(-1 - i);
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
        using var admin = await ConnectionMultiplexer.ConnectAsync(connection + ",allowAdmin=true");
        // The doorbells, as another node hears them: INFO commandstats on Managed Redis is not exact enough to count them.
        long doorbells = 0;
        var channel = RedisChannel.Pattern(app + "*");
        // Off unless asked: each delivery to this counter costs this process a thread-pool item of its own.
        if (CountingDoorbells)
        {
            await admin.GetSubscriber().SubscribeAsync(channel, (_, _) => Interlocked.Increment(ref doorbells));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Interlocked.Exchange(ref doorbells, 0);
        var commands0 = Commands(admin);
        using var process = Process.GetCurrentProcess();
        var cpu0 = process.TotalProcessorTime;
        var items0 = ThreadPool.CompletedWorkItemCount;
        var locks0 = Monitor.LockContentionCount;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < ops; i++)
        {
            await Op(i);
        }

        var wall = watch.Elapsed;
        var items = ThreadPool.CompletedWorkItemCount - items0;
        var locks = Monitor.LockContentionCount - locks0;
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpu0;
        var heard = Interlocked.Read(ref doorbells);
        var commands = Commands(admin);
        Console.WriteLine(CountingDoorbells ? string.Create(CultureInfo.InvariantCulture, $"  {cell} doorbells/op: {heard / (double)ops:F2}") : $"  {cell} doorbells/op: n/a");
        Console.WriteLine(
            "  " + cell + " commands/op: " + (commands is null || commands0 is null || commands.Endpoints != commands0.Endpoints
                ? "n/a (the two snapshots did not cover the same endpoints)"
                : string.Join(" ", commands.Totals.Select(kv => (kv.Key, D: (kv.Value - commands0.Totals.GetValueOrDefault(kv.Key)) / (double)ops)).Where(x => x.D >= 0.05).OrderByDescending(x => x.D).Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Key}={x.D:F2}")))));
        await host.StopAsync();
        return (wall.TotalMilliseconds / ops, cpu.TotalMicroseconds / ops, items / (double)ops, locks / (double)ops);
    }

    /// <summary>Calls per command over the primaries, from INFO commandstats, with the endpoints it covered; null when the server refuses it.</summary>
    private static CommandSnapshot? Commands(ConnectionMultiplexer admin)
    {
        var totals = new Dictionary<string, long>();
        var covered = new List<string>();
        try
        {
            foreach (var endpoint in admin.GetEndPoints())
            {
                IServer server;
                try
                {
                    server = admin.GetServer(endpoint);
                }
                catch (ArgumentException)
                {
                    // An endpoint the cluster map dropped meanwhile.
                    continue;
                }

                if (server.IsReplica)
                {
                    continue;
                }

                covered.Add(endpoint.ToString() ?? string.Empty);
                foreach (var (key, value) in server.Info("commandstats").SelectMany(g => g))
                {
                    var calls = value.Split(',')[0].Split('=')[1];
                    var name = key.Replace("cmdstat_", string.Empty, StringComparison.Ordinal);
                    totals[name] = totals.GetValueOrDefault(name) + long.Parse(calls, CultureInfo.InvariantCulture);
                }
            }
        }
        catch (RedisException)
        {
            // Not allowed here: the table goes without commands.
            return null;
        }

        return new CommandSnapshot(totals, string.Join(",", covered.Order(StringComparer.Ordinal)));
    }

    private static async Task CleanupAsync(string connection, string app)
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(connection);
        var deleted = 0;
        foreach (var endpoint in mux.GetEndPoints())
        {
            IServer server;
            try
            {
                server = mux.GetServer(endpoint);
            }
            catch (ArgumentException)
            {
                // An endpoint the cluster map dropped meanwhile.
                continue;
            }

            if (server.IsReplica)
            {
                continue;
            }

            var batch = new List<Task>();
            foreach (var key in server.Keys(pattern: $"{app}*", pageSize: 1000))
            {
                batch.Add(mux.GetDatabase().KeyDeleteAsync(key));
                deleted++;
            }

            await Task.WhenAll(batch);
        }

        Console.WriteLine($"deleted {deleted} keys under {app}");
    }

    private sealed record CommandSnapshot(Dictionary<string, long> Totals, string Endpoints);
}
