using System.Diagnostics;
using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.Benchmarks;

/// <summary>Server CPU and commands per one-field hash write with a deadline: a MULTI/EXEC transaction, one script, and a plain pipeline as the floor.</summary>
internal static class HashWriteCostHarness
{
    private const int Rounds = 20000;
    private const string Script = "redis.call('HSET', KEYS[1], ARGV[1], ARGV[2]) return redis.call('PEXPIREAT', KEYS[1], ARGV[3])";
    private const string VersionScript = "redis.call('HSET', KEYS[1], ARGV[1], ARGV[2]) local v = redis.pcall('HINCRBY', KEYS[1], '_version_', 1) redis.call('PEXPIREAT', KEYS[1], ARGV[3]) return v";

    public static async Task RunAsync(string endpoint)
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(endpoint + ",allowAdmin=true");
        var db = mux.GetDatabase();
        var server = mux.GetServer(mux.GetEndPoints()[0]);
        var key = $"bench:hashwrite:{Guid.NewGuid():N}";
        var deadline = DateTime.UtcNow.AddMinutes(30);
        var at = new DateTimeOffset(deadline).ToUnixTimeMilliseconds();

        var plainSha = await server.ScriptLoadAsync(Script);
        var sha = await server.ScriptLoadAsync(VersionScript);
        var shapes = new (string Name, Func<int, Task> Write)[]
        {
            ("MULTI/EXEC (today)", async i =>
            {
                var t = db.CreateTransaction();
                _ = t.HashSetAsync(key, [new HashEntry($"ip:{i % 250}", "1")]);
                _ = t.KeyExpireAsync(key, deadline);
                await t.ExecuteAsync();
            }),
            ("EVALSHA (HSET + PEXPIREAT)", i => db.ScriptEvaluateAsync(plainSha, [key], [$"ip:{i % 250}", "1", at])),
            ("MULTI/EXEC + HINCRBY", async i =>
            {
                var t = db.CreateTransaction();
                _ = t.HashSetAsync(key, [new HashEntry($"ip:{i % 250}", "1")]);
                var version = t.HashIncrementAsync(key, "_version_");
                _ = t.KeyExpireAsync(key, deadline);
                await t.ExecuteAsync();
                await version;
            }),
            ("EVALSHA script + HINCRBY", i => db.ScriptEvaluateAsync(sha, [key], [$"ip:{i % 250}", "1", at])),
            ("Pipeline, no MULTI (floor)", i => Task.WhenAll(db.HashSetAsync(key, [new HashEntry($"ip:{i % 250}", "1")]), db.KeyExpireAsync(key, deadline))),
        };

        for (var cycle = 0; cycle < 3; cycle++)
        {
            foreach (var (name, write) in shapes)
            {
                for (var i = 0; i < 500; i++)
                {
                    await write(i);
                }

                var cpu = Cpu(server);
                var before = Commands(server);
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < Rounds; i++)
                {
                    await write(i);
                }

                var us = watch.Elapsed.TotalMicroseconds / Rounds;
                var cpuUs = (Cpu(server) - cpu) * 1e6 / Rounds;
                var after = Commands(server);
                var perCall = string.Join(" ", after.Select(kv => (kv.Key, Delta: kv.Value - before.GetValueOrDefault(kv.Key))).Where(x => x.Delta > 10).Select(x => $"{x.Key}={x.Delta / (double)Rounds:F2}"));
                Console.WriteLine($"{name,-28} {us,7:F1} us/write  redis cpu {cpuUs,5:F1} us/write  {perCall}");
            }
        }

        await db.KeyDeleteAsync(key);
    }

    private static double Cpu(IServer server)
    {
        var cpu = server.Info("cpu").SelectMany(g => g).ToDictionary(p => p.Key, p => p.Value);
        return double.Parse(cpu["used_cpu_user"], CultureInfo.InvariantCulture) + double.Parse(cpu["used_cpu_sys"], CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, long> Commands(IServer server)
    {
        var result = new Dictionary<string, long>();
        foreach (var pair in server.Info("commandstats").SelectMany(g => g))
        {
            result[pair.Key.Replace("cmdstat_", string.Empty)] = long.Parse(pair.Value.Split(',')[0].Split('=')[1], CultureInfo.InvariantCulture);
        }

        return result;
    }
}
