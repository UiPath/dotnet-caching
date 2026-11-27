using System.Reflection;

namespace UiPath.Caching.Tests.Fakes;

internal static class CacheWork
{
    /// <summary>Waits for the work in flight now, leaving a run a test holds open on purpose.</summary>
    public static async Task SnapshotAsync(MultilayerCacheBase cache)
    {
        var reads = Field<InFlight<InFlightKey, object?>>(cache, typeof(MultilayerCacheBase), "_innerReads");
        var generations = Field<InFlight<InFlightKey, object?>>(cache, cache.GetType(), "_generations");
        var writes = Field<BackgroundWrites>(cache, typeof(MultilayerCacheBase), "_background");
        await Task.WhenAll(reads.Completions().Concat(generations.Completions()).Concat(writes.Completions())).WaitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Waits for the work in flight now, then until no run is left.</summary>
    public static async Task CompleteAsync(MultilayerCacheBase cache)
    {
        await SnapshotAsync(cache);
        await RunTables.SettledAsync(cache, () => cache switch
        {
            MultilayerCache layered => layered.RunsInFlight,
            MultilayerHashCache hashed => hashed.RunsInFlight,
            _ => 0,
        }).WaitAsync(TestContext.Current.CancellationToken);
    }

    private static T Field<T>(object owner, Type type, string name) =>
        (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
