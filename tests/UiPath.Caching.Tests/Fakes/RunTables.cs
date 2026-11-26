using System.Collections.Concurrent;
using System.Reflection;
using UiPath.Caching.Tests.Broadcast;
using UiPath.Caching.Tests.Fakes;
using Xunit.v3;

[assembly: RunTablesDrainAfterEachTest]

namespace UiPath.Caching.Tests.Fakes;

internal static class RunTables
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly ConcurrentDictionary<string, ConcurrentBag<TrackedCache>> Tracked = new();

    public static MultilayerCache Track(MultilayerCache cache) => Track(cache, () => cache.RunsInFlight);

    public static MultilayerHashCache Track(MultilayerHashCache cache) => Track(cache, () => cache.RunsInFlight);

    internal static IReadOnlyCollection<TrackedCache> Take(string testId) =>
        Tracked.TryRemove(testId, out var caches) ? caches : [];

    internal static void ObserveCompletions()
    {
        if (TestContext.Current.Test?.UniqueID is { } id && Tracked.TryGetValue(id, out var caches))
        {
            foreach (var cache in caches)
            {
                ObserveCompletions(cache.Cache);
            }
        }
    }

    internal static async Task SettledAsync(MultilayerCacheBase cache, Func<int> count)
    {
        var settled = OperationSignals.Shared.WhenAsync(() => count() == 0);
        if (!settled.IsCompleted)
        {
            ObserveCompletions(cache);
        }
        await settled;
        count().Should().Be(0);
    }

    internal static Task RefreshesSettledAsync(MultilayerCacheBase cache) => SettledAsync(cache, () =>
    {
        var background = (BackgroundWrites)typeof(MultilayerCacheBase).GetField("_background", InstanceFields)!.GetValue(cache)!;
        var refresher = typeof(MultilayerCacheBase).GetField("_peerRefresher", InstanceFields)!.GetValue(cache);
        var pending = refresher is null ? 0 : (int)refresher.GetType().GetProperty("Pending", InstanceFields)!.GetValue(refresher)!;
        return background.InFlight + pending;
    });

    private static T Track<T>(T cache, Func<int> count) where T : MultilayerCacheBase
    {
        if (TestContext.Current.Test?.UniqueID is { } testId)
        {
            Tracked.GetOrAdd(testId, _ => []).Add(new(cache, count));
        }
        return cache;
    }

    private static void ObserveCompletions(MultilayerCacheBase cache)
    {
        foreach (var name in new[] { "_innerReads", "_generations" })
        {
            var flights = cache.GetType().GetField(name, InstanceFields)!.GetValue(cache)!;
            var completions = (Task[])flights.GetType().GetMethod("Completions", InstanceFields)!.Invoke(flights, null)!;
            foreach (var completion in completions)
            {
                Observe(completion);
            }
        }
        var background = typeof(MultilayerCacheBase).GetField("_background", InstanceFields)!.GetValue(cache)!;
        var gate = background.GetType().GetField("_gate", InstanceFields)!.GetValue(background)!;
        var enter = gate.GetType().GetMethod("Enter");
        if (enter is null)
        {
            Monitor.Enter(gate);
        }
        else
        {
            enter.Invoke(gate, null);
        }
        try
        {
            if (((BackgroundWrites)background).InFlight > 0)
            {
                var field = background.GetType().GetField("_idle", InstanceFields)!;
                var idle = (TaskCompletionSource?)field.GetValue(background);
                if (idle is null || idle.Task.IsCompleted)
                {
                    idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    field.SetValue(background, idle);
                }
                Observe(idle.Task);
            }
        }
        finally
        {
            if (enter is null)
            {
                Monitor.Exit(gate);
            }
            else
            {
                gate.GetType().GetMethod("Exit")!.Invoke(gate, null);
            }
        }
        OperationSignals.Shared.Completed();
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(static _ => OperationSignals.Shared.Completed(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    internal sealed record TrackedCache(MultilayerCacheBase Cache, Func<int> Count);
}

[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class RunTablesDrainAfterEachTestAttribute : BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        foreach (var cache in RunTables.Take(test.UniqueID))
        {
            RunTables.SettledAsync(cache.Cache, cache.Count).GetAwaiter().GetResult();
        }
    }
}
