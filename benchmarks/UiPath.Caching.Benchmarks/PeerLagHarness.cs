using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace UiPath.Caching.Benchmarks;

/// <summary>How much later another node sees a write, per doorbell setting: a writer and a peer host, the peer holding every key in its local tier and reading them in a tight loop. Single writes and bursts of 8 concurrent writes; a sample is the time from the start of the write to the peer reading the new value. Keys live under a fresh AppShortName and are deleted afterwards.</summary>
internal static class PeerLagHarness
{
    private static readonly TimeSpan RewriteWindow = TimeSpan.FromSeconds(2);

    public static async Task RunAsync(int seconds, int rounds, string cells)
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? throw new InvalidOperationException("REDIS_CONNECTION_STRING is not set");
        var app = $"rca311{Guid.NewGuid():N}"[..14];
        var names = cells.Split(',');
        foreach (var cell in names)
        {
            ExtraFor(app, string.Empty, cell);
        }

        Console.WriteLine($"app prefix {app}; each key is rewritten no sooner than {RewriteWindow.TotalSeconds:F0} s; {seconds} s per cell");
#pragma warning disable CA1303 // the header row is built from constants only; console output of a harness is not localized
        Console.WriteLine("cell        shape   round  writes   seen    p50_ms   p95_ms   p99_ms   max_ms  burst_p50  burst_p95  keys  bursts  incomplete");
#pragma warning restore CA1303
        for (var round = 0; round < rounds; round++)
        {
            foreach (var cell in names)
            {
                foreach (var burst in new[] { 1, 8 })
                {
                    await MeasureAsync(connection, app, cell, burst, seconds, round);
                }
            }
        }

        await DoorbellCostHarness.CleanupAsync(app);
    }

    private static Dictionary<string, string?> ExtraFor(string app, string connection, string cell)
    {
        var notify = cell == "nonotify" ? "false" : cell == "notify" || cell.StartsWith("notify:", StringComparison.Ordinal) ? "true" : throw new ArgumentException($"Unknown cell {cell}", nameof(cell));
        var extra = new Dictionary<string, string?>
        {
            { "Caching:AppShortName", app },
            { "Caching:Connections:Redis:ConnectionString", connection },
            { "Caching:Broadcast:RedisStreams:Enabled", "true" },
            { "Caching:Broadcast:RedisStreams:NotifyEnabled", notify },
            { "Logging:LogLevel:Default", "Warning" },
            { "Logging:LogLevel:StackExchange.Redis", "Error" },
        };
        if (cell.StartsWith("notify:", StringComparison.Ordinal))
        {
            extra["Caching:Broadcast:RedisStreams:NotifyMinInterval"] = TimeSpan.FromMilliseconds(int.Parse(cell["notify:".Length..], CultureInfo.InvariantCulture)).ToString("c", CultureInfo.InvariantCulture);
        }

        return extra;
    }

    private static async Task MeasureAsync(string connection, string app, string cell, int burst, int seconds, int round)
    {
        var extra = ExtraFor(app, connection, cell);
        using var writerHost = HostHelper.GetHost(0, cache: KnownCacheProviderNames.InMemoryRedis, topic: "RedisStreams", extra: extra);
        using var peerHost = HostHelper.GetHost(1, cache: KnownCacheProviderNames.InMemoryRedis, topic: "RedisStreams", extra: extra);
        await writerHost.StartAsync();
        await peerHost.StartAsync();
        var writer = writerHost.Services.GetRequiredService<ICache<string>>();
        var peer = peerHost.Services.GetRequiredService<ICache<string>>();
        var run = Guid.NewGuid().ToString("N")[..8];
        // A write every 10 ms, or a burst of 8 every 20 ms; a key is not rewritten within RewriteWindow, far past the slowest poll, so an unseen write is lost rather than overwritten.
        var period = TimeSpan.FromMilliseconds(burst == 1 ? 10 : 20);
        var keys = (int)Math.Ceiling(RewriteWindow / period) * burst;
        var names = Enumerable.Range(0, keys).Select(i => $"{run}:{i}").ToArray();
        foreach (var name in names)
        {
            await writer.SetAsync(name, "init", TimeSpan.FromMinutes(5));
            _ = await peer.GetAsync(name);
        }

        // The group, the subscription and the poll come up in the background: wait until a write reaches the peer.
        await Task.Delay(TimeSpan.FromSeconds(2));
        await writer.SetAsync(names[0], "warm", TimeSpan.FromMinutes(5));
        var until = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 10);
        while (await peer.GetAsync(names[0]) != "warm" && Stopwatch.GetTimestamp() < until)
        {
            await Task.Delay(1);
        }

        var lastSeen = new string?[keys];
        for (var i = 0; i < keys; i++)
        {
            lastSeen[i] = await peer.GetAsync(names[i]);
        }

        var samples = new List<double>();
        var bursts = new Dictionary<long, (int Seen, double Max)>();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                for (var i = 0; i < keys; i++)
                {
                    var v = await peer.GetAsync(names[i]);
                    if (v is null || v == lastSeen[i])
                    {
                        continue;
                    }

                    lastSeen[i] = v;
                    if (v.StartsWith('t') && long.TryParse(v.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sent))
                    {
                        var ms = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                        lock (samples)
                        {
                            samples.Add(ms);
                            var (seen, max) = bursts.GetValueOrDefault(sent);
                            bursts[sent] = (seen + 1, Math.Max(max, ms));
                        }
                    }
                }
            }
        });

        var end = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * seconds);
        var written = 0;
        var tasks = new Task[burst];
        while (Stopwatch.GetTimestamp() < end)
        {
            var started = Stopwatch.GetTimestamp();
            var value = "t" + started.ToString(CultureInfo.InvariantCulture);
            for (var w = 0; w < burst; w++)
            {
                tasks[w] = writer.SetAsync(names[(written + w) % keys], value, TimeSpan.FromMinutes(5)).AsTask();
            }

            await Task.WhenAll(tasks);
            written += burst;
            var left = period - Stopwatch.GetElapsedTime(started);
            if (left > TimeSpan.Zero)
            {
                await Task.Delay(left);
            }
        }

        // The slowest settings poll every 250 ms; give the last writes time to land.
        await Task.Delay(TimeSpan.FromSeconds(1));
        await stop.CancelAsync();
        await reader;
        await peerHost.StopAsync();
        await writerHost.StopAsync();

        double[] s, b;
        int started0, incomplete;
        lock (samples)
        {
            s = [.. samples.Order()];
            b = [.. bursts.Values.Where(x => x.Seen == burst).Select(x => x.Max).Order()];
            started0 = bursts.Count;
            incomplete = bursts.Values.Count(x => x.Seen < burst);
        }

        static double P(double[] x, double q) => x.Length == 0 ? double.NaN : x[Math.Min(x.Length - 1, (int)(x.Length * q))];
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{cell,-11} {"x" + burst,-7} {round,5}  {written,6} {s.Length,6}  {P(s, 0.5),8:F2} {P(s, 0.95),8:F2} {P(s, 0.99),8:F2} {P(s, 1),8:F2}  {P(b, 0.5),9:F2}  {P(b, 0.95),9:F2}  {keys,4}  {started0,6}  {incomplete,10}"));
        if (s.Length != written || incomplete > 0 || b.Length + incomplete != bursts.Count)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  WARNING {cell} x{burst} round {round}: {written} writes, {s.Length} seen, {incomplete} incomplete bursts, {(written / burst) - bursts.Count} bursts never seen; the percentiles leave out what was not seen"));
        }
    }
}
