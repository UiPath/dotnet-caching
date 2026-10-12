using System.Collections.Concurrent;
using System.Reflection;
using UiPath.Caching.Tests.Fakes;
using Xunit.v3;

[assembly: RunTablesDrainAfterEachTest]

namespace UiPath.Caching.Tests.Fakes;

/// <summary>The caches a test built, so the run tables can be checked once it ends: a run or reservation an exit path forgot would stay in the table for good.</summary>
internal static class RunTables
{
    private static readonly ConcurrentDictionary<string, ConcurrentBag<Func<int>>> Tracked = new();

    public static MultilayerCache Track(MultilayerCache cache) => Track(cache, () => cache.RunsInFlight);

    public static MultilayerHashCache Track(MultilayerHashCache cache) => Track(cache, () => cache.RunsInFlight);

    internal static IReadOnlyCollection<Func<int>> Take(string testId) =>
        Tracked.TryRemove(testId, out var counts) ? counts : [];

    private static T Track<T>(T cache, Func<int> runsInFlight)
    {
        if (TestContext.Current.Test?.UniqueID is { } testId)
        {
            Tracked.GetOrAdd(testId, _ => []).Add(runsInFlight);
        }

        return cache;
    }
}

/// <summary>Fails a test that leaves a run in a tracked cache's tables. Background work such as a rehydration may still be finishing, so it waits a little before it judges.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class RunTablesDrainAfterEachTestAttribute : BeforeAfterTestAttribute
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        var counts = RunTables.Take(test.UniqueID);
        if (counts.Count == 0)
        {
            return;
        }

        var deadline = DateTime.UtcNow + Grace;
        int left;
        while ((left = counts.Sum(c => c())) > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        if (left > 0)
        {
            throw new InvalidOperationException($"{left} run(s) or inner read(s) were still in a cache's tables {Grace.TotalSeconds:0} s after the test ended.");
        }
    }
}
