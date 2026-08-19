namespace UiPath.Caching.Tests;

public class CompositeCacheKeyTests
{
    [Fact]
    public void Single_key_is_returned_as_is_so_batches_serialize_with_single_key_callers()
    {
        CompositeCacheKey.For([(CacheKey)"a"]).Should().Be((CacheKey)"a");
    }

    [Fact]
    public void Order_does_not_change_the_composite()
    {
        CompositeCacheKey.For([(CacheKey)"a", (CacheKey)"b"])
            .Should().Be(CompositeCacheKey.For([(CacheKey)"b", (CacheKey)"a"]));
    }

    [Fact]
    public void Different_sets_produce_different_composites()
    {
        CompositeCacheKey.For([(CacheKey)"a", (CacheKey)"b"])
            .Should().NotBe(CompositeCacheKey.For([(CacheKey)"a", (CacheKey)"c"]));
    }

    [Fact]
    public void Composite_is_prefixed_and_stable_across_calls()
    {
        var first = CompositeCacheKey.For([(CacheKey)"a", (CacheKey)"b"]);
        var second = CompositeCacheKey.For([(CacheKey)"a", (CacheKey)"b"]);

        first.Should().Be(second);
        first.Name.Should().StartWith("batch:");
    }

    [Fact]
    public void Keys_that_would_collide_when_naively_concatenated_stay_distinct()
    {
        CompositeCacheKey.For([(CacheKey)"ab", (CacheKey)"c"])
            .Should().NotBe(CompositeCacheKey.For([(CacheKey)"a", (CacheKey)"bc"]));
    }

    [Fact]
    public void Empty_set_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => CompositeCacheKey.For([]));
    }
}
