using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

// Alone in its collection: a meter listener another test holds makes every local hit allocate, and a hit's own cost is what this measures.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationCollection
{
    public const string Name = "Allocation";
}

[Collection(AllocationCollection.Name)]
public sealed class LocalHitAllocationTests : RouterTestBase
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);

    // One delegate for the whole class, so the allocation test measures the cache and not the delegate.
    private static readonly Func<int, CancellationToken, Task<Item?>> NullGenerator = static (_, _) => Task.FromResult<Item?>(null);

    [Fact]
    public async Task A_get_or_add_that_hits_the_local_tier_allocates_no_closure_for_its_generator_or_its_state()
    {
        var node = NewNode(localCap: TimeSpan.FromMinutes(1));
        ICache<Item> cache = node.Cache;
        await cache.SetAsync("k", new Item("1"), Life, Ct);
        await cache.GetAsync("k", Ct);
        for (var i = 0; i < 2000; i++)
        {
            await cache.GetOrAddAsync("k", 7, NullGenerator, Ct);
        }

        var perCall = long.MaxValue;
        for (var run = 0; run < 7; run++)
        {
            perCall = Math.Min(perCall, BytesPerLocalHit(cache));
        }

        perCall.Should().BeLessThan(64, "a closure and a delegate would add about 90 bytes");
    }

    private static long BytesPerLocalHit(ICache<Item> cache)
    {
        const int Calls = 1000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Calls; i++)
        {
            var hit = cache.GetOrAddAsync("k", 7, NullGenerator, Ct);
            if (!hit.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("A hit of the local tier completes at once.");
            }
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / Calls;
    }
}
