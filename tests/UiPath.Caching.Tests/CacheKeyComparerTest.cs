namespace UiPath.Caching.Tests;

public class CacheKeyComparerTest
{
    [Fact]
    public void Sensitive_distinguishes_case()
    {
        var upper = new CacheKey("AbC", CacheKeyCasing.Sensitive);
        var lower = new CacheKey("abc", CacheKeyCasing.Sensitive);
        CacheKeyComparer.Sensitive.Equals(upper, lower).Should().BeFalse();
        CacheKeyComparer.Sensitive.Equals(upper, new CacheKey("AbC", CacheKeyCasing.Sensitive)).Should().BeTrue();
    }

    [Fact]
    public void Insensitive_folds_case()
    {
        var upper = new CacheKey("AbC", CacheKeyCasing.Sensitive);
        var lower = new CacheKey("abc", CacheKeyCasing.Sensitive);
        CacheKeyComparer.Insensitive.Equals(upper, lower).Should().BeTrue();
        CacheKeyComparer.Insensitive.GetHashCode(upper).Should().Be(CacheKeyComparer.Insensitive.GetHashCode(lower));
    }

    [Fact]
    public void Hash_is_consistent_with_equals()
    {
        var a = new CacheKey("session:x", CacheKeyCasing.Sensitive);
        var b = new CacheKey("session:x", CacheKeyCasing.Insensitive);
        CacheKeyComparer.Sensitive.Equals(a, b).Should().BeTrue();
        CacheKeyComparer.Sensitive.GetHashCode(a).Should().Be(CacheKeyComparer.Sensitive.GetHashCode(b));
    }

    [Fact]
    public void Works_as_hashset_comparer()
    {
        var set = new HashSet<CacheKey>(CacheKeyComparer.Insensitive)
        {
            new("AbC", CacheKeyCasing.Sensitive),
        };
        set.Add(new CacheKey("abc", CacheKeyCasing.Sensitive)).Should().BeFalse();
        set.Should().HaveCount(1);
    }

    [Fact]
    public void Singletons_are_cached()
    {
        CacheKeyComparer.Sensitive.Should().BeSameAs(CacheKeyComparer.Sensitive);
        CacheKeyComparer.Insensitive.Should().BeSameAs(CacheKeyComparer.Insensitive);
    }

    [Fact]
    public void Default_key_does_not_throw()
    {
        CacheKeyComparer.Sensitive.GetHashCode(default(CacheKey)).Should().Be(CacheKeyComparer.Sensitive.GetHashCode(default(CacheKey)));
        CacheKeyComparer.Insensitive.Equals(default, default).Should().BeTrue();
    }
}
