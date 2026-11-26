using System.Reflection;

namespace UiPath.Caching.Tests.Fakes;

internal static class CacheWork
{
    public static async Task CompleteAsync(MultilayerCacheBase cache)
    {
        var reads = Field<InFlight<InFlightKey, object?>>(cache, typeof(MultilayerCacheBase), "_innerReads");
        var generations = Field<InFlight<InFlightKey, object?>>(cache, cache.GetType(), "_generations");
        var writes = Field<BackgroundWrites>(cache, typeof(MultilayerCacheBase), "_background");
        await Task.WhenAll(reads.Completions().Concat(generations.Completions()).Concat(writes.Completions()));
    }

    private static T Field<T>(object owner, Type type, string name) =>
        (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
