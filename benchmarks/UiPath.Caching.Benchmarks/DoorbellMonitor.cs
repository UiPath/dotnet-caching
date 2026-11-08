using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.Benchmarks;

/// <summary>Counts the doorbells on channels whose name contains <c>part</c>, and keeps the running total in a file, for a benchmark process to read before and after a run.</summary>
internal static class DoorbellMonitor
{
    public static async Task RunAsync(string part, string file)
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? throw new InvalidOperationException("REDIS_CONNECTION_STRING is not set");
        using var mux = await ConnectionMultiplexer.ConnectAsync(connection);
        long count = 0;
        await mux.GetSubscriber().SubscribeAsync(RedisChannel.Pattern("*"), (channel, _) =>
        {
            if (channel.ToString().Contains(part, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref count);
            }
        });
        var temp = file + ".tmp";
        while (true)
        {
            await File.WriteAllTextAsync(temp, Interlocked.Read(ref count).ToString(CultureInfo.InvariantCulture));
            File.Move(temp, file, overwrite: true);
            await Task.Delay(100);
        }
    }
}
