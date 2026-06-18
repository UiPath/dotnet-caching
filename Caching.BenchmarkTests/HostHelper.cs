using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UiPath.Platform.Caching;
using UiPath.Platform.Caching.CloudEvents;
using UiPath.Platform.Caching.Config;
using UiPath.Platform.Caching.Polly;
using UiPath.Platform.Caching.Redis;

namespace Caching.BenchmarkTests;

internal static class SetupHelper
{
    public static Entry<T>[] Setup<T>(int hostsNo,
        string? cacheType,
        string? topicType,
        int numKeys,
        Func<T> factory)
    {
        var entries = Enumerable.Range(0, hostsNo).Select(i => GetEntry<T>(i, cacheType, topicType)).ToArray();
        var connector = entries[0].Host.Services.GetRequiredService<IRedisConnector>();
        connector.Database.Execute("FLUSHALL", args: Array.Empty<object>(), flags: StackExchange.Redis.CommandFlags.DemandMaster);
        var tasks = new List<Task<bool>>();
        void WaitAll()
        {
            Task.WaitAll([.. tasks]);
            tasks.Clear();
        }
        for (var i = 0; i < numKeys; i++)
        {
            var cache = entries[Random.Shared.Next(0, entries.Length)].Cache;
            var t = cache.SetAsync($"key_{i}", factory(), CancellationToken.None).AsTask();
            tasks.Add(t);
            if (tasks.Count > 100)
            {
                WaitAll();
            }
        }

        WaitAll();
        return entries;
    }

    public static void Cleanup<T>(Entry<T>[] entries)
    {
        foreach (var entry in entries)
        {
            try
            {
                entry.Host.Dispose();
                entry.Start.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }


    private static Entry<T> GetEntry<T>(int i, string? cacheType, string? topicType)
    {
        var host = HostHelper.GetHost(i, cacheType, topicType);
        var start = host.StartAsync();
        var cache = host.Services.GetRequiredService<ICache<T>>();
        return new Entry<T>(host, cache, start);
    }
}

internal static class HostHelper
{
    private static Lazy<string> BasePath = new Lazy<string>(() =>
    {
        var currentDir = Directory.GetCurrentDirectory();
        var temp = new DirectoryInfo(currentDir);
        while (!string.Equals(temp!.Name, "Caching", StringComparison.InvariantCultureIgnoreCase))
        {
            temp = temp.Parent;
        }
        return Path.Combine(temp.FullName, "Sample.AspNetCore");
    });

    public static IHost GetHost(int i, string? cache = null, string? topic = null, IDictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            { "Caching:SourceUri", $"urn:m{i}" },
            { "Caching:DefaultCache", cache ?? "InMemoryRedis" },
            { "Caching:DefaultTopic", topic ?? "RedisStreams" },
            { "Caching:InMemoryRedis:TrackStatistics", "false" },
            { "Caching:InMemory:TrackStatistics", "false" },
            { "Caching:TelemetryEnabled", "false" },
            { "Caching:Connections:Redis:ProfilerEnabled", "false" },
        };
        if (extra is not null)
        {
            foreach (var kv in extra)
            {
                settings[kv.Key] = kv.Value;
            }
        }
        var builder = new HostBuilder()
        .ConfigureAppConfiguration(c =>
        {
            c.SetBasePath(BasePath.Value)
             .AddJsonFile("appsettings.json", optional: false)
             .AddInMemoryCollection(settings)
            .AddEnvironmentVariables();
        })

        .ConfigureLogging(x => x.AddConsole())
        .ConfigureCaching(x =>
            x.AddRedisConnection()
            .AddBroadcast()
            .AddRedis()
            .AddInMemoryRedis()
            .AddMemory()
            .AddResilienceStrategies()
            .AddCloudEvents());
        return builder.Build();
    }
}
