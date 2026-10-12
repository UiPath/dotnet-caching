using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

public class SpanKeyRemoveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_span_remove_clears_the_entry_the_normalized_key_names()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await cache.SetAsync<string>("user:42", "v", policy: null, Ct)).Should().BeTrue();
        (await cache.SetAsync<string>("user:43", "v", policy: null, Ct)).Should().BeTrue();
        (await typed.SetAsync("user:42", "v", Ct)).Should().BeTrue();

        Remove<string>(cache, " User:42 ").Should().BeTrue();
        Remove(typed, " User:42 ").Should().BeTrue();

        (await cache.ContainsAsync<string>("user:42", Ct)).Should().BeFalse();
        (await typed.ContainsAsync("user:42", Ct)).Should().BeFalse();
        (await cache.ContainsAsync<string>("user:43", Ct)).Should().BeTrue("another key is left alone");
    }

    [Fact]
    public async Task A_span_remove_clears_a_hash_the_normalized_key_names()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var typed = new HashCache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var hash = new Dictionary<string, string?> { ["f"] = "v" };
        (await cache.SetAsync<string>("user:42", hash, policy: null, Ct)).Should().BeTrue();
        (await typed.SetAsync("user:42", hash, Ct)).Should().BeTrue();

        Remove<string>(cache, " User:42 ").Should().BeTrue();
        Remove(typed, " User:42 ").Should().BeTrue();

        (await cache.ContainsAsync<string>("user:42", Ct)).Should().BeFalse();
        (await typed.ContainsAsync("user:42", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_cache_that_is_mocked_is_removed_through_its_CacheKey_overload()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        var hash = Substitute.For<IHashCache>();
        var typedHash = Substitute.For<IHashCache<string>>();
        cache.RemoveAsync<string>(new CacheKey("user:42"), Ct).Returns(true);
        typed.RemoveAsync(new CacheKey("user:42"), Ct).Returns(true);
        hash.RemoveAsync<string>(new CacheKey("user:42"), Ct).Returns(true);
        typedHash.RemoveAsync(new CacheKey("user:42"), Ct).Returns(true);

        Remove<string>(cache, "User:42").Should().BeTrue();
        Remove(typed, "User:42").Should().BeTrue();
        Remove<string>(hash, "User:42").Should().BeTrue();
        Remove(typedHash, "User:42").Should().BeTrue();

        await cache.Received(1).RemoveAsync<string>(new CacheKey("user:42"), Ct);
    }

    private static bool Remove<T>(ICache cache, string key)
    {
        Span<char> buffer = stackalloc char[64];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.RemoveAsync<T>(buffer[..key.Length], Ct));
    }

    private static bool Remove<T>(ICache<T> cache, string key)
    {
        Span<char> buffer = stackalloc char[64];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.RemoveAsync(buffer[..key.Length], Ct));
    }

    private static bool Remove<T>(IHashCache cache, string key)
    {
        Span<char> buffer = stackalloc char[64];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.RemoveAsync<T>(buffer[..key.Length], Ct));
    }

    private static bool Remove<T>(IHashCache<T> cache, string key)
    {
        Span<char> buffer = stackalloc char[64];
        key.AsSpan().CopyTo(buffer);
        return Complete(cache.RemoveAsync(buffer[..key.Length], Ct));
    }

    private static bool Complete(ValueTask<bool> remove) =>
        remove.IsCompletedSuccessfully ? remove.Result : remove.AsTask().GetAwaiter().GetResult();
}
