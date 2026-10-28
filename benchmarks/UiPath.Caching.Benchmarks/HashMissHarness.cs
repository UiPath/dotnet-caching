using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

/// <summary>Remove, one-field read miss, one-field HashReplace write and read back, timed per step, on a fresh host and then warm.</summary>
internal static class HashMissHarness
{
    public static async Task RunAsync(string topic, int rounds)
    {
        for (var h = 0; h < 3; h++)
        {
            Console.WriteLine($"--- host {h}");
            await RunHostAsync(topic, h == 0 ? rounds : 1);
        }
    }

    private static async Task RunHostAsync(string topic, int rounds)
    {
        var extra = new Dictionary<string, string?> { { "Caching:Connections:Redis:ConnectionString", "127.0.0.1:6379,abortConnect=false" } };
        using var host = HostHelper.GetHost(0, cache: KnownCacheProviderNames.InMemoryRedis, topic: topic, extra: extra);
        var build = Stopwatch.StartNew();
        await host.StartAsync();
        Console.WriteLine($"host start: {build.Elapsed.TotalMilliseconds:F1} ms");
        var cache = host.Services.GetRequiredService<IHashCache<string>>();
        var claims = host.Services.GetRequiredService<ICache<bool?>>();
        var key = $"bench:hashmiss:{Guid.NewGuid():N}";
        var options = new HashCacheEntryOptions(DateTimeOffset.UtcNow.AddMinutes(10), null, null, HashCacheSetOption.HashReplace);
        for (var i = 0; i < rounds; i++)
        {
            var lang = i % 2 == 0 ? "en" : "ro";
            var sw = Stopwatch.StartNew();
            await cache.RemoveAsync(key);
            var t1 = sw.Elapsed.TotalMilliseconds;
            var miss = await cache.GetItemAsync(key, lang);
            var t2 = sw.Elapsed.TotalMilliseconds;
            await cache.SetAsync(key, new Dictionary<string, string?> { [lang] = "v" }, options);
            var t3 = sw.Elapsed.TotalMilliseconds;
            var hit = await cache.GetItemAsync(key, lang);
            var t4 = sw.Elapsed.TotalMilliseconds;
            await claims.TryAddAsync($"{key}:claim{i}", true, TimeSpan.FromMinutes(1));
            var t5 = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"round {i,2}: remove {t1:F1}  miss {t2 - t1:F1}  set {t3 - t2:F1}  get {t4 - t3:F1}  claim {t5 - t4:F1}  total {t5:F1} ms (miss={miss is null}, hit={hit})");
        }

        await cache.RemoveAsync(key);
    }
}
