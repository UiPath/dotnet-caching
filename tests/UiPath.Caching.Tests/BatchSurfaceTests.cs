namespace UiPath.Caching.Tests;

public class BatchSurfaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Keys_of_known_size_read_as_a_read_only_collection_with_their_composed_keys()
    {
        CacheKey[] source = ["a", "b"];
        var keys = (ICollection<CacheKey>)ComposedKeys<CacheKey>.Create(source, static k => k, static k => (CacheKey)$"p:{k.Name}", static (_, k) => k);

        keys.Count.Should().Be(2);
        keys.IsReadOnly.Should().BeTrue();
        keys.Contains("p:a").Should().BeTrue();
        var copy = new CacheKey[3];
        keys.CopyTo(copy, 1);
        copy.Skip(1).Select(k => k.Name).Should().Equal("p:a", "p:b");
        ((Action)(() => keys.Add("c"))).Should().Throw<NotSupportedException>();
        ((Action)keys.Clear).Should().Throw<NotSupportedException>();
        ((Action)(() => keys.Remove("a"))).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task The_null_caches_store_nothing_and_so_fail_nothing()
    {
        ICache cache = NullCache.Instance;
        IHashCache hashes = NullHashCache.Instance;

        (await cache.RemoveManyAsync<string>(["a"], Ct)).Should().BeEmpty();
        (await cache.SetManyAsync<string>([new("a", "v")], (CachePolicy?)null, Ct)).Should().BeEmpty();
        (await cache.SetManyAsync<string>([new("a", "v")], TimeSpan.FromMinutes(1), null, Ct)).Should().BeEmpty();
        (await cache.SetManyAsync<string>([new("a", "v")], DateTimeOffset.UtcNow.AddMinutes(1), null, Ct)).Should().BeEmpty();
        (await hashes.RemoveManyAsync<string>(["a"], Ct)).Should().BeEmpty();
        (await hashes.SetManyAsync<string>([new("a", new Dictionary<string, string?> { ["f"] = "v" })], new HashCacheEntryOptions(), null, Ct)).Should().BeEmpty();
        (await hashes.RemoveFieldsAsync<string>("a", ["f"], Ct)).Should().BeTrue();
    }
}
