using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;

namespace UiPath.Caching.Benchmarks;

// Open-loop request-path latency for a per-org IHashCache<string> hash holding `status` + up to 250 `ip:<n>` fields, on two InMemoryRedis nodes.
// A request is GetOrAddAsync(org) and, on WriteShare of the calls, an awaited one-field HashReplace SetAsync(org, ip:<next>).
//
// Variants: a = broadcast off, b = streams + AutoAck + PeerFieldUpdates, c = pubsub + PeerFieldUpdates,
//           d = b without PeerFieldUpdates, e = b with only node 1 writing (node 2 only reads).
// Run with:  dotnet run -c Release -- churn [endpoint] [variant] [seconds] [ratePerNode] [orgs] [warmupSeconds]
// Env: ZIPF=1 picks orgs by a zipf(1) law instead of uniform; WRITE_SHARE overrides 0.44.
internal static class ChurnLoadHarness
{
    private const int IpsPerOrg = 250;

    private static readonly int[] NodeIds = [0, 1];

    private static readonly Func<CancellationToken, Task<IDictionary<string, string?>>> Generator =
        _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["status"] = "active" });

    public static async Task RunAsync(string endpoint, string variant, int seconds, int rate, int orgs, int warmup)
    {
        var writeShare = double.Parse(Environment.GetEnvironmentVariable("WRITE_SHARE") ?? "0.44", CultureInfo.InvariantCulture);
        if (seconds < 1 || rate < 1 || rate > Stopwatch.Frequency || orgs < 1 || warmup < 0 || writeShare is < 0 or > 1)
        {
            await Console.Error.WriteLineAsync("churn: need seconds >= 1, 1 <= ratePerNode <= Stopwatch.Frequency, orgs >= 1, warmupSeconds >= 0, 0 <= WRITE_SHARE <= 1");
            Environment.ExitCode = 2;
            return;
        }

        var zipf = Environment.GetEnvironmentVariable("ZIPF") == "1";
        var (topic, broadcast, peer, writers) = variant switch
        {
            "a" => ("RedisStreams", false, true, 2),
            "b" => ("RedisStreams", true, true, 2),
            "c" => ("RedisPubSub", true, true, 2),
            "d" => ("RedisStreams", true, false, 2),
            "e" => ("RedisStreams", true, true, 1),
            _ => throw new ArgumentException($"unknown variant '{variant}'"),
        };
        var extra = new Dictionary<string, string?>
        {
            { "Caching:Connections:Redis:ConnectionString", endpoint + ",abortConnect=false" },
            { "Caching:InMemoryRedis:PeerFieldUpdates", peer ? "true" : "false" },
            { "Caching:BroadcastEnabled", broadcast ? "true" : "false" },
            { "Caching:Broadcast:RedisStreams:AutoAck", "true" },
            { "Caching:Policies:System.String:BackgroundDistributedWrite", Environment.GetEnvironmentVariable("BG") == "0" ? "false" : "true" },
            { "Caching:Broadcast:RedisPubSub:Enabled", topic == "RedisPubSub" ? "true" : "false" },
        };
        using var mux = await ConnectionMultiplexer.ConnectAsync(endpoint + ",allowAdmin=true");
        var a = HostHelper.GetHost(1, cache: KnownCacheProviderNames.InMemoryRedis, topic: topic, extra: extra);
        var b = HostHelper.GetHost(2, cache: KnownCacheProviderNames.InMemoryRedis, topic: topic, extra: extra);
        await a.StartAsync();
        await b.StartAsync();
        var run = $"churn:{Guid.NewGuid():N}:";
        var deadline = DateTimeOffset.UtcNow.AddHours(2);
        var options = new HashCacheEntryOptions(deadline, null, null, HashCacheSetOption.HashReplace);
        var capacity = (int)((long)rate * (seconds + 5) * 11 / 10);
        var nodes = NodeIds.Select(i => new Node(i, (i == 0 ? a : b).Services.GetRequiredService<IHashCache<string>>(), i < writers, capacity, orgs)).ToArray();
        var policy = (i: 0, p: (a.Services.GetRequiredService<ICachePolicyFactory>()).Resolve(typeof(string).FullName!));
        Console.WriteLine($"variant={variant} topic={topic} broadcast={broadcast} peer={peer} writers={writers} rate={rate}/node orgs={orgs} {(zipf ? "zipf" : "uniform")} writeShare={writeShare} bgWrite={policy.p?.BackgroundDistributedWrite} pid={Environment.ProcessId}");
        foreach (var n in nodes)
        {
            n.Hook = PeerHook.TryInstall(n.Cache, n.Stats);
        }

        var cdf = zipf ? Cdf(orgs) : null;
        await Seed(nodes[0].Cache, run, options, orgs);
        var origin = Stopwatch.GetTimestamp();
        var windowStart = origin + warmup * Stopwatch.Frequency;
        var ctx = new Ctx(run, options, writeShare, cdf, orgs, origin, windowStart, windowStart + seconds * Stopwatch.Frequency);
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(() => Sample(nodes, stop.Token));
        var server = mux.GetServer(mux.GetEndPoints()[0]);

        var dispatchers = nodes.Select(n => new Thread(() => Dispatch(n, ctx, rate, stop.Token)) { IsBackground = true }).ToArray();
        foreach (var d in dispatchers) { d.Start(); }
        await Task.Delay(Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), ctx.WindowStart));

        foreach (var n in nodes) { n.Reset(); }
        var cmdBefore = Commands(server);
        var gc = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var contention = Monitor.LockContentionCount;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        await Task.Delay(Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), ctx.WindowEnd));
        wall.Stop();
        var cmdAfter = Commands(server);
        var peerAt = nodes.Select(n => n.Stats.Snapshot()).ToArray();
        var cpuUsed = Process.GetCurrentProcess().TotalProcessorTime - cpu;
        var contended = Monitor.LockContentionCount - contention;
        var gcDelta = (GC.CollectionCount(0) - gc.Item1, GC.CollectionCount(1) - gc.Item2, GC.CollectionCount(2) - gc.Item3);
        var joined = dispatchers.All(d => d.Join(TimeSpan.FromSeconds(warmup + 10)));
        await stop.CancelAsync();
        await sampler;
        var drain = Stopwatch.StartNew();
        while (nodes.Sum(n => Volatile.Read(ref n.InFlight)) > 0 && drain.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(50);
        }

        var incomplete = nodes.Select(n => Math.Max(0, Volatile.Read(ref n.Issued) - Volatile.Read(ref n.Completed) - Volatile.Read(ref n.Errors))).ToArray();
        var invalid = !joined || incomplete.Sum() > 0 || nodes.Sum(n => Volatile.Read(ref n.InFlight)) > 0;
        if (invalid)
        {
            Console.WriteLine($"INVALID: the drain timed out or dispatchers did not finish; {incomplete.Sum()} requests issued in the window never completed (counted as failures)");
        }

        var warmupErrors = nodes.Sum(n => Volatile.Read(ref n.WarmupErrors));
        if (warmupErrors > 0)
        {
            Console.WriteLine($"warmup/late errors (not counted): {warmupErrors}");
        }

#pragma warning disable CA1303 // the header row is built from constants only; console output of a harness is not localized
        Console.WriteLine($"{"node",-5}{"issued",8}{"done",8}{"err",5}{"incompl",8}{"lag max",8} | {"resp p50",9}{"p95",8}{"p99",8} | {"svc p50",8}{"p95",8}{"p99",8} | {"goa p95",8}{"set p50",8}{"p95",8} | peer: {"rcv",7}{"applied",8}{"dropped",8}{"avg us",8}{"max us",8}");
#pragma warning restore CA1303
        foreach (var n in nodes)
        {
            var svc = n.Svc.Sorted();
            var resp = n.Resp.Sorted();
            var goa = n.Goa.Sorted();
            var set = n.Set.Sorted();
            var s = peerAt[n.Id];
            var rcv = s.Applied + s.Dropped;
            Console.WriteLine($"n{n.Id + 1,-4}{n.Issued,8}{n.Completed,8}{n.Errors,5}{incomplete[n.Id],8}{Ms(n.MaxLag),8} | {Ms(Pct(resp, .5)),9}{Ms(Pct(resp, .95)),8}{Ms(Pct(resp, .99)),8} | {Ms(Pct(svc, .5)),8}{Ms(Pct(svc, .95)),8}{Ms(Pct(svc, .99)),8} | {Ms(Pct(goa, .95)),8}{Ms(Pct(set, .5)),8}{Ms(Pct(set, .95)),8} | {(n.Hook ? "" : "(no hook) ")}{rcv,7}{s.Applied,8}{s.Dropped,8}{(rcv == 0 ? 0 : s.TotalTicks * 1e6 / Stopwatch.Frequency / rcv),8:F1}{s.MaxTicks * 1e6 / Stopwatch.Frequency,8:F0}");
        }

        foreach (var n in nodes.Where(n => peerAt[n.Id].Reasons.Length > 0))
        {
            Console.WriteLine($"n{n.Id + 1} drop reasons: " + string.Join("; ", peerAt[n.Id].Reasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        }

        var delta = cmdAfter.Select(kv => (Name: kv.Key, Calls: kv.Value - cmdBefore.GetValueOrDefault(kv.Key))).Where(x => x.Calls > 0 && x.Name is not ("info" or "ping" or "client" or "cluster")).OrderByDescending(x => x.Calls).Take(16);
        Console.WriteLine("redis cmds (both nodes, " + $"{wall.Elapsed.TotalSeconds:F0}s): " + string.Join(" ", delta.Select(x => $"{x.Name}={x.Calls}")));
        var samples = nodes.Select(n => n.PoolSamples).Sum();
        Console.WriteLine($"process: cpu={cpuUsed.TotalSeconds / wall.Elapsed.TotalSeconds:F2} cores, gc0/1/2={gcDelta.Item1}/{gcDelta.Item2}/{gcDelta.Item3}, monitor contentions={contended}, pool pending avg/max={(samples == 0 ? 0 : nodes.Sum(n => n.PoolPendingSum) / (double)samples):F1}/{nodes.Max(n => n.PoolPendingMax)}, threads max={nodes.Max(n => n.PoolThreadsMax)}");
        var mergedResp = Merge(nodes, n => n.Resp);
        var mergedSvc = Merge(nodes, n => n.Svc);
        Console.WriteLine($"RESULT variant={variant} resp_p50={Ms(Pct(mergedResp, .5))} resp_p95={Ms(Pct(mergedResp, .95))} resp_p99={Ms(Pct(mergedResp, .99))} svc_p50={Ms(Pct(mergedSvc, .5))} svc_p95={Ms(Pct(mergedSvc, .95))} svc_p99={Ms(Pct(mergedSvc, .99))} issued={nodes.Sum(n => n.Issued)} done={nodes.Sum(n => n.Completed)} err={nodes.Sum(n => n.Errors) + incomplete.Sum()} lag_max={Ms(nodes.Max(n => n.MaxLag))}{(invalid ? " INVALID" : string.Empty)}");

        await a.StopAsync();
        await b.StopAsync();
        a.Dispose();
        b.Dispose();
        foreach (var key in server.Keys(pattern: "*" + run + "*", pageSize: 500))
        {
            await mux.GetDatabase().KeyDeleteAsync(key, CommandFlags.FireAndForget);
        }
    }

    private static void Dispatch(Node node, Ctx ctx, int rate, CancellationToken token)
    {
        var period = Stopwatch.Frequency / rate;
        var next = ctx.Origin;
        while (next < ctx.WindowEnd && !token.IsCancellationRequested)
        {
            var now = Stopwatch.GetTimestamp();
            if (now < next)
            {
                Pacer.Pause(next - now);
                continue;
            }

            if (next >= ctx.WindowStart)
            {
                node.MaxLag = Math.Max(node.MaxLag, now - next);
                Interlocked.Increment(ref node.Issued);
            }

            Interlocked.Increment(ref node.InFlight);
            ThreadPool.UnsafeQueueUserWorkItem(new Request(node, ctx, next), preferLocal: false);
            next += period;
        }
    }

    private static async Task Seed(IHashCache<string> cache, string run, HashCacheEntryOptions options, int orgs)
    {
        await Parallel.ForEachAsync(Enumerable.Range(0, orgs), new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (org, token) =>
        {
            var key = run + org;
            await cache.GetOrAddAsync(key, Generator, token);
            await cache.SetAsync(key, Enumerable.Range(0, IpsPerOrg).ToDictionary(ip => IpField(org, ip), _ => (string?)"1"), options, token);
        });
    }

    private static string IpField(int org, int ip) => $"ip:10.{org / 250}.{org % 250}.{ip}";

    private static async Task Sample(Node[] nodes, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(50, token); } catch (OperationCanceledException) { return; }
            var pending = ThreadPool.PendingWorkItemCount;
            var threads = ThreadPool.ThreadCount;
            var node = nodes[0];
            node.PoolSamples++;
            node.PoolPendingSum += pending;
            node.PoolPendingMax = Math.Max(node.PoolPendingMax, pending);
            node.PoolThreadsMax = Math.Max(node.PoolThreadsMax, threads);
        }
    }

    private static Dictionary<string, long> Commands(IServer server)
    {
        var result = new Dictionary<string, long>();
        foreach (var group in server.Info("commandstats"))
        {
            foreach (var kv in group)
            {
                var calls = kv.Value.Split(',')[0];
                if (kv.Key.StartsWith("cmdstat_", StringComparison.Ordinal) && calls.StartsWith("calls=", StringComparison.Ordinal))
                {
                    result[kv.Key["cmdstat_".Length..]] = long.Parse(calls["calls=".Length..], CultureInfo.InvariantCulture);
                }
            }
        }

        return result;
    }

    private static double[] Cdf(int n)
    {
        var cdf = new double[n];
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            sum += 1.0 / (i + 1);
            cdf[i] = sum;
        }

        for (var i = 0; i < n; i++)
        {
            cdf[i] /= sum;
        }

        return cdf;
    }

    private static long[] Merge(Node[] nodes, Func<Node, Samples> pick) =>
        nodes.SelectMany(n => pick(n).Sorted()).Order().ToArray();

    private static long Pct(long[] sorted, double p) =>
        sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];

    private static string Ms(long ticks) =>
        (ticks * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);

    // The library's peer-apply delegate takes internal types; an expression tree compiles a timing wrapper around it without library changes.
    internal static class PeerHook
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security Hotspot", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "The harness reads the cache's private memory tier on purpose.")]
        public static bool TryInstall(object cache, PeerStats stats)
        {
            var hash = Find(cache, "MultilayerHashCache");
            var setter = hash?.GetType().GetField("_localMemorySetter", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(hash);
            var prop = setter?.GetType().GetProperty("PeerApply", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (setter is null || prop?.GetValue(setter) is not Delegate original)
            {
                return false;
            }

            var type = prop.PropertyType;
            var args = type.GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
            var started = Expression.Variable(typeof(long));
            var result = Expression.Variable(typeof(bool));
            var reason = Expression.Variable(typeof(string));
            var diag = Environment.GetEnvironmentVariable("DIAG") == "1";
            var classify = new Classifier(hash!);
            var body = Expression.Block(
                [started, result, reason],
                Expression.Assign(reason, diag ? Expression.Call(Expression.Constant(classify), typeof(Classifier).GetMethod(nameof(Classifier.Classify))!, Expression.Convert(args[0], typeof(object)), Expression.Convert(args[1], typeof(object))) : Expression.Constant("")),
                Expression.Assign(started, Expression.Call(typeof(Stopwatch).GetMethod(nameof(Stopwatch.GetTimestamp))!)),
                Expression.Assign(result, Expression.Invoke(Expression.Constant(original, type), args)),
                Expression.Call(Expression.Constant(stats), typeof(PeerStats).GetMethod(nameof(PeerStats.Reason))!, result, reason),
                Expression.Call(Expression.Constant(stats), typeof(PeerStats).GetMethod(nameof(PeerStats.Record))!, result, Expression.Subtract(Expression.Call(typeof(Stopwatch).GetMethod(nameof(Stopwatch.GetTimestamp))!), started)),
                result);
            prop.SetValue(setter, Expression.Lambda(type, body, args).Compile());
            return true;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security Hotspot", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "The harness reads the cache's private memory tier on purpose.")]
        private static object? Find(object root, string typeName)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var queue = new Queue<(object Value, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                var (value, depth) = queue.Dequeue();
                if (value.GetType().Name == typeName)
                {
                    return value;
                }

                if (depth >= 4)
                {
                    continue;
                }

                for (var t = value.GetType(); t is not null; t = t.BaseType)
                {
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    {
                        if (!f.FieldType.IsValueType && f.FieldType != typeof(string) && f.GetValue(value) is { } child && seen.Add(child))
                        {
                            queue.Enqueue((child, depth + 1));
                        }
                    }
                }
            }

            return null;
        }

        // Why a peer event cannot be applied to what the node holds, read from the held entry just before the library decides.
        public sealed class Classifier(object hash)
        {
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Security Hotspot", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "The harness reads the cache's private memory tier on purpose.")]
            private readonly object _memory = hash.GetType().GetField("_memoryCache", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(hash)
                ?? hash.GetType().BaseType!.GetField("_memoryCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(hash)!;

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Security Hotspot", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "The harness reads the cache's private memory tier on purpose.")]
            public string Classify(object state, object update)
            {
                var key = state.GetType().GetProperty("CacheKey")!.GetValue(state)!;
                var name = (string)key.GetType().GetProperty("Name")!.GetValue(key)!;
                var eventVersion = (long)update.GetType().GetProperty("Version")!.GetValue(update)!;
                var args = new object?[] { name, null };
                var found = (bool)_memory.GetType().GetMethods().First(m => m.Name == "TryGetValue").Invoke(_memory, args)!;
                if (!found || args[1] is null)
                {
                    return "not held";
                }

                var stamps = hash.GetType().Assembly.GetType("UiPath.Caching.HashVersions")!;
                var tryGet = new object?[] { args[1], null };
                if (!(bool)stamps.GetMethod("TryGet", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, tryGet)!)
                {
                    return "held but unstamped (no version)";
                }

                var resolve = new object?[] { 0L, null };
                if (!(bool)tryGet[1]!.GetType().GetMethod("TryResolve", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tryGet[1], resolve)!)
                {
                    return "stamp no longer valid (own write failed or out of step)";
                }

                var number = (long)resolve[0]!;
                var ahead = ((Array)resolve[1]!).Length;
                if (number == 0)
                {
                    return "stamp waits on a replace that has not returned";
                }

                if (number == eventVersion - 1)
                {
                    return "in step (dropped for another reason)";
                }

                if (number < eventVersion - 1)
                {
                    return $"behind by >1 write (own writes ahead: {Math.Min(ahead, 2)})";
                }

                return $"held version >= event version (own writes ahead: {Math.Min(ahead, 2)})";
            }
        }
    }

    internal sealed record PeerSnapshot(long Applied, long Dropped, long TotalTicks, long MaxTicks, KeyValuePair<string, long>[] Reasons);

    internal sealed class PeerStats
    {
        private long _applied, _dropped, _ticks, _max;

        public long Applied => Interlocked.Read(ref _applied);
        public long Dropped => Interlocked.Read(ref _dropped);
        public long TotalTicks => Interlocked.Read(ref _ticks);
        public long MaxTicks => Interlocked.Read(ref _max);

        public System.Collections.Concurrent.ConcurrentDictionary<string, long> Reasons { get; } = new();

        public PeerSnapshot Snapshot() => new(Applied, Dropped, TotalTicks, MaxTicks, [.. Reasons]);

        public void Reset()
        {
            Interlocked.Exchange(ref _applied, 0);
            Interlocked.Exchange(ref _dropped, 0);
            Interlocked.Exchange(ref _ticks, 0);
            Interlocked.Exchange(ref _max, 0);
            Reasons.Clear();
        }

        public void Reason(bool applied, string reason)
        {
            if (!applied)
            {
                Reasons.AddOrUpdate(reason, 1, static (_, n) => n + 1);
            }
        }

        public void Record(bool applied, long ticks)
        {
            Interlocked.Increment(ref applied ? ref _applied : ref _dropped);
            Interlocked.Add(ref _ticks, ticks);
            while (true)
            {
                var seen = Interlocked.Read(ref _max);
                if (ticks <= seen || Interlocked.CompareExchange(ref _max, ticks, seen) == seen)
                {
                    break;
                }
            }
        }
    }

    private sealed class Request(Node node, Ctx ctx, long intended) : IThreadPoolWorkItem
    {
        public void Execute() => _ = ExecuteAsync();

        private async Task ExecuteAsync()
        {
            var start = Stopwatch.GetTimestamp();
            long goa = 0, set = 0;
            var failed = false;
            var rnd = Random.Shared;
            try
            {
                int org;
                if (ctx.Cumulative is { } cdf)
                {
                    var i = Array.BinarySearch(cdf, rnd.NextDouble());
                    org = i < 0 ? Math.Min(~i, ctx.Orgs - 1) : i;
                }
                else
                {
                    org = rnd.Next(ctx.Orgs);
                }

                var key = ctx.Run + org;
                await node.Cache.GetOrAddAsync(key, Generator, CancellationToken.None);
                var afterGet = Stopwatch.GetTimestamp();
                goa = afterGet - start;
                if (node.Writes && rnd.NextDouble() < ctx.WriteShare)
                {
                    var c = Interlocked.Increment(ref node.IpCounters[org]);
                    var ip = (int)((c * 2 + node.Id) % IpsPerOrg);
                    await node.Cache.SetAsync(key, new Dictionary<string, string?> { [IpField(org, ip)] = "1" }, ctx.Options);
                    set = Stopwatch.GetTimestamp() - afterGet;
                }
            }
            catch (Exception ex)
            {
                failed = true;
                if (Interlocked.Increment(ref node.ErrorsSeen) <= 3)
                {
                    Console.WriteLine($"error: {ex.GetType().Name}: {ex.Message[..Math.Min(160, ex.Message.Length)]}");
                }
            }
            finally
            {
                var done = Stopwatch.GetTimestamp();
                if (intended >= ctx.WindowStart && intended < ctx.WindowEnd)
                {
                    if (failed)
                    {
                        Interlocked.Increment(ref node.Errors);
                    }
                    else
                    {
                        Interlocked.Increment(ref node.Completed);
                        node.Svc.Add(done - start);
                        node.Resp.Add(done - intended);
                        node.Goa.Add(goa);
                        if (set > 0)
                        {
                            node.Set.Add(set);
                        }
                    }
                }
                else if (failed)
                {
                    Interlocked.Increment(ref node.WarmupErrors);
                }

                Interlocked.Decrement(ref node.InFlight);
            }
        }
    }

    private sealed record Ctx(string Run, HashCacheEntryOptions Options, double WriteShare, double[]? Cumulative, int Orgs, long Origin, long WindowStart, long WindowEnd);

    private sealed class Node(int id, IHashCache<string> cache, bool writes, int capacity, int orgs)
    {
        public bool Hook;
        public int InFlight;
        public long Issued, Completed, Errors, WarmupErrors, ErrorsSeen, MaxLag;
        public long PoolSamples, PoolPendingSum, PoolPendingMax, PoolThreadsMax;

        public int Id { get; } = id;
        public IHashCache<string> Cache { get; } = cache;
        public bool Writes { get; } = writes;
        public long[] IpCounters { get; } = new long[orgs];
        public Samples Svc { get; } = new(capacity);
        public Samples Resp { get; } = new(capacity);
        public Samples Goa { get; } = new(capacity);
        public Samples Set { get; } = new(capacity);
        public PeerStats Stats { get; } = new();

        public void Reset()
        {
            Stats.Reset();
            PoolSamples = PoolPendingSum = PoolPendingMax = PoolThreadsMax = 0;
        }
    }

    private sealed class Samples(int capacity)
    {
        private readonly long[] _values = new long[capacity];
        private int _count;
        private int _written;

        public void Add(long value)
        {
            var i = Interlocked.Increment(ref _count) - 1;
            if (i < _values.Length)
            {
                _values[i] = value;
                Interlocked.Increment(ref _written);
            }
        }

        // Read after the drain; a slot claimed but not yet written (a timed-out drain) is left out.
        public long[] Sorted()
        {
            var n = Math.Min(Volatile.Read(ref _count), _values.Length);
            var copy = _values.AsSpan(0, n).ToArray();
            if (Volatile.Read(ref _written) < n)
            {
                copy = copy.Where(v => v != 0).ToArray();
            }

            Array.Sort(copy);
            return copy;
        }
    }
}
