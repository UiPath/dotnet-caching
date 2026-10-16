using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>The blocking GetOrAdd wrappers pass their generator as state to a static lambda; a real cache and a proxy answer them as before.</summary>
public class SyncExtensionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_sync_wrappers_run_the_generator_on_a_miss_and_not_on_a_hit()
    {
        using var inner = InMemoryMultilayer.Cache();
        var cache = new Cache<string>(inner);
        using var innerHash = InMemoryMultilayer.HashCache();
        var hash = new HashCache<string>(innerHash);
        var calls = 0;
        string? Load()
        {
            calls++;
            return "v";
        }

        IDictionary<string, string?> LoadHash()
        {
            calls++;
            return new Dictionary<string, string?> { ["f"] = "v" };
        }

        KeyValuePair<CacheKey, string?>[] LoadMany(CacheKey[] keys)
        {
            calls++;
            return [.. keys.Select(k => new KeyValuePair<CacheKey, string?>(k, "m"))];
        }

        cache.GetOrAdd("a", Load, Ct).Should().Be("v");
        cache.GetOrAdd("a", Load, TimeSpan.FromMinutes(1), Ct).Should().Be("v");
        cache.GetOrAdd("b", Load, DateTimeOffset.UtcNow.AddMinutes(1), Ct).Should().Be("v");
        hash.GetOrAdd("h", LoadHash, Ct)["f"].Should().Be("v");
        hash.GetOrAdd("h", LoadHash, TimeSpan.FromMinutes(1), Ct)["f"].Should().Be("v");
        cache.GetOrAdd(["c", "d"], LoadMany, Ct).Select(e => e.Value).Should().Equal("m", "m");
        cache.GetOrAdd(["c", "d"], LoadMany, TimeSpan.FromMinutes(1), Ct).Select(e => e.Value).Should().Equal("m", "m");

        calls.Should().Be(4, "one miss each for a, b, h and the batch");
    }

    [Fact]
    public void A_proxy_answers_the_sync_wrappers_from_its_closure_setups()
    {
        var cache = Substitute.For<ICache<string>>();
        ValueTaskStubs.Returns(
            cache.GetOrAddAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<string?>>>(), Ct),
            call => new ValueTask<string?>(call.Arg<Func<CancellationToken, Task<string?>>>()(Ct)));
        ValueTaskStubs.Returns(
            cache.GetOrAddAsync(Arg.Any<KeyValuePair<CacheKey, CacheKey>[]>(), Arg.Any<Func<CacheKey[], CancellationToken, Task<KeyValuePair<CacheKey, string?>[]>>>(), Ct),
            call => new ValueTask<KeyValuePair<CacheKey, string?>[]>(call.Arg<Func<CacheKey[], CancellationToken, Task<KeyValuePair<CacheKey, string?>[]>>>()([.. call.Arg<KeyValuePair<CacheKey, CacheKey>[]>().Select(e => e.Value)], Ct)));
        var hash = Substitute.For<IHashCache<string>>();
        ValueTaskStubs.Returns(
            hash.GetOrAddAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Ct),
            call => new ValueTask<IDictionary<string, string?>>(call.Arg<Func<CancellationToken, Task<IDictionary<string, string?>>>>()(Ct)));

        cache.GetOrAdd("k", () => "v", Ct).Should().Be("v");
        cache.GetOrAdd(["k"], keys => [.. keys.Select(k => new KeyValuePair<CacheKey, string?>(k, "m"))], Ct).Single().Value.Should().Be("m");
        hash.GetOrAdd("k", () => new Dictionary<string, string?> { ["f"] = "v" }, Ct)["f"].Should().Be("v");
    }
}
