using UiPath.Caching;

namespace UiPath.Caching.Tests;

public class NullCacheConditionalAddTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TryAdd_reports_added_for_every_caller()
    {
        var sut = NullCache.Instance;

        (await sut.TryAddAsync("k", "first", policy: null, token: Ct)).Should().BeTrue();
        (await sut.TryAddAsync("k", "second", policy: null, token: Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task TryAdd_reports_added_whatever_the_expiration()
    {
        var ttl = TimeSpan.FromMinutes(5);

        (await NullCache.Instance.TryAddAsync("k", "v", token: Ct)).Should().BeTrue();
        (await NullCache.Instance.TryAddAsync("k", "v", ttl, token: Ct)).Should().BeTrue();
        (await NullCache.Instance.TryAddAsync("k", "v", DateTimeOffset.UtcNow.Add(ttl), token: Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task SetAsync_still_reports_success()
    {
        (await NullCache.Instance.SetAsync("k", "v", policy: null, token: Ct)).Should().BeTrue();
    }

    [Fact]
    public void An_uncacheable_type_still_throws()
    {
        var act = () => NullCache.Instance.TryAddAsync<int>("k", 1, policy: null, token: Ct);

        act.Should().Throw<NotCacheableException>();
    }
}
