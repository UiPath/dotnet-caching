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

    [Theory]
    [InlineData(null)]
    [InlineData(5)]
    public async Task TryAdd_reports_added_whatever_the_expiration(int? minutes)
    {
        TimeSpan? ttl = minutes is { } m ? TimeSpan.FromMinutes(m) : null;

        (await NullCache.Instance.TryAddAsync("k", "v", ttl, token: Ct)).Should().BeTrue();
        (await NullCache.Instance.TryAddAsync("k", "v", ttl.HasValue ? DateTimeOffset.UtcNow.Add(ttl.Value) : null, token: Ct)).Should().BeTrue();
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
