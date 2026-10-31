using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>A hit on the Redis-only cache: GetAsync against the GetOrAdd shapes, timed and counted in Redis commands.</summary>
internal static class RedisHitHarness
{
    private const int Rounds = 20000;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S8969:Remove this null-forgiving operator; the compiler already knows this expression is not null here", Justification = "The operator is required: without it Task<string> does not convert to Task<string?> under the nullable compiler.")]
    public static async Task RunAsync(string endpoint)
    {
        var connection = endpoint + ",abortConnect=false";
        var extra = new Dictionary<string, string?> { { "Caching:Connections:Redis:ConnectionString", connection }, { "Caching:Broadcast:RedisPubSub:Enabled", "true" }, { "Caching:Redis:ReadScriptsEnabled", Environment.GetEnvironmentVariable("READSCRIPTS") ?? "false" } };
        using var host = HostHelper.GetHost(0, cache: KnownCacheProviderNames.Redis, topic: "RedisPubSub", extra: extra);
        await host.StartAsync();
        var cache = host.Services.GetRequiredService<ICache<string>>();
        using var admin = await ConnectionMultiplexer.ConnectAsync(connection + ",allowAdmin=true");
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        var raw = host.Services.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        static Task<string> Load() => Task.FromResult("loader-ran");
        var key = $"bench:redishit:{Guid.NewGuid():N}";
        await cache.SetAsync(key, "token", TimeSpan.FromMinutes(30));

        var shapes = new (string Name, Func<ValueTask<string?>> Call)[]
        {
            ("GetAsync", () => cache.GetAsync(key)),
            ("GetCacheEntry (L2 read of a local miss)", async () => (await raw.GetCacheEntryAsync<string>(key, policy: null)).Value),
            ("GetOrAddWithExpiration", () => cache.GetOrAddWithExpirationAsync(key, Load, static async (f, _) => new GeneratedValue<string>(await f(), DateTimeOffset.UtcNow.AddMinutes(50)))),
            ("GetOrAdd(state)", () => cache.GetOrAddAsync(key, Load, static (f, _) => f()!)),
        };

        foreach (var (name, call) in shapes)
        {
            if (await call() != "token")
            {
                throw new InvalidOperationException($"{name} did not return the stored value");
            }
        }

        for (var cycle = 0; cycle < 3; cycle++)
        {
            foreach (var (name, call) in shapes)
            {
                for (var i = 0; i < 300; i++)
                {
                    await call();
                }

                var cpuBefore = Cpu(server);
                var before = Commands(server);
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < Rounds; i++)
                {
                    if (await call() != "token")
                    {
                        throw new InvalidOperationException($"{name} missed during the timed loop");
                    }
                }

                var us = watch.Elapsed.TotalMicroseconds / Rounds;
                var after = Commands(server);
                var cpuUs = (Cpu(server) - cpuBefore) * 1e6 / Rounds;
                var perCall = string.Join(" ", after.Select(kv => (kv.Key, Delta: kv.Value - before.GetValueOrDefault(kv.Key))).Where(x => x.Delta > 0).Select(x => $"{x.Key}={x.Delta / (double)Rounds:F2}"));
                Console.WriteLine($"{name,-40} {us,8:F1} us/call  redis cpu {cpuUs,6:F1} us/call  redis per call: {perCall}");
            }
        }

        await cache.RemoveAsync(key);
        await host.StopAsync();
    }

    private static double Cpu(IServer server)
    {
        var cpu = server.Info("cpu").SelectMany(g => g).ToDictionary(p => p.Key, p => p.Value);
        return double.Parse(cpu["used_cpu_user"], System.Globalization.CultureInfo.InvariantCulture) + double.Parse(cpu["used_cpu_sys"], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, long> Commands(IServer server)
    {
        var result = new Dictionary<string, long>();
        foreach (var group in server.Info("commandstats"))
        {
            foreach (var pair in group)
            {
                var calls = pair.Value.Split(',')[0].Split('=')[1];
                result[pair.Key.Replace("cmdstat_", string.Empty)] = long.Parse(calls, CultureInfo.InvariantCulture);
            }
        }

        return result;
    }
}
