using System.Diagnostics;
using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.Benchmarks;

/// <summary>What one doorbell costs this process, by part: the PUBLISH reply, the timeout the topic puts on it, and the node's own subscription hearing it. Raw StackExchange.Redis on a fresh channel; one PUBLISH per op, awaited.</summary>
internal static class PublishCostHarness
{
    public static async Task RunAsync(int ops, int rounds)
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? throw new InvalidOperationException("REDIS_CONNECTION_STRING is not set");
        using var mux = await ConnectionMultiplexer.ConnectAsync(connection);
        var db = mux.GetDatabase();
        var channelName = $"rca311pub{Guid.NewGuid():N}";
        var channel = RedisChannel.Literal(channelName);
        var sharded = RedisChannel.Sharded(channelName);
        Console.WriteLine($"endpoints {mux.GetEndPoints().Length}: " + string.Join(" ", mux.GetEndPoints().Select(e => mux.GetServer(e)).Select(x => $"{x.ServerType}/{(x.IsReplica ? "replica" : "primary")}")));
        long heard = 0;
        var cells = new (string Name, bool Subscribed, Func<Task> Publish)[]
        {
            ("spublish+timeout+self", true, () => db.PublishAsync(sharded, "x", CommandFlags.DemandMaster).WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None)),
            ("publish", false, () => db.PublishAsync(channel, "x", CommandFlags.DemandMaster)),
            ("publish+timeout", false, () => db.PublishAsync(channel, "x", CommandFlags.DemandMaster).WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None)),
            ("publish+timeout+self", true, () => db.PublishAsync(channel, "x", CommandFlags.DemandMaster).WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None)),
            ("fireandforget+self", true, () => db.PublishAsync(channel, "x", CommandFlags.FireAndForget | CommandFlags.DemandMaster)),
            ("ping", false, () => db.PingAsync()),
        };
#pragma warning disable CA1303 // the header row is built from constants only; console output of a harness is not localized
        Console.WriteLine("cell                  round  items/op  cpu_us/op  heard/op");
#pragma warning restore CA1303
        for (var round = 0; round < rounds; round++)
        {
            foreach (var (name, subscribed, publish) in cells)
            {
                void Handler(RedisChannel _, RedisValue __) => Interlocked.Increment(ref heard);
                var target = name.StartsWith("spublish", StringComparison.Ordinal) ? sharded : channel;
                if (subscribed)
                {
                    await mux.GetSubscriber().SubscribeAsync(target, Handler);
                }

                for (var i = 0; i < 100; i++)
                {
                    await publish();
                }

                // A fire-and-forget PUBLISH is still on the wire: a PING behind it lets the deliveries land before the window closes.
                await db.PingAsync();
                await Task.Delay(200);
                Interlocked.Exchange(ref heard, 0);
                var process = Process.GetCurrentProcess();
                var cpu0 = process.TotalProcessorTime;
                var items0 = ThreadPool.CompletedWorkItemCount;
                for (var i = 0; i < ops; i++)
                {
                    await publish();
                }

                await db.PingAsync();
                await Task.Delay(200);
                var items = ThreadPool.CompletedWorkItemCount - items0;
                process.Refresh();
                var cpu = process.TotalProcessorTime - cpu0;
                if (subscribed)
                {
                    await mux.GetSubscriber().UnsubscribeAsync(target, Handler);
                }

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name,-21} {round,5}  {items / (double)ops,8:F2}  {cpu.TotalMicroseconds / ops,9:F1}  {Interlocked.Read(ref heard) / (double)ops,8:F2}"));
            }
        }
    }
}
