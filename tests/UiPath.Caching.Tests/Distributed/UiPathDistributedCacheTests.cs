using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Distributed;

namespace UiPath.Caching.Tests.Distributed;

public class UiPathDistributedCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Payload = [1, 2, 3];

    private readonly ICache _inner = Substitute.For<ICache>();
    private readonly ISystemClock _clock = Substitute.For<ISystemClock>();
    private readonly UiPathDistributedCache _cache;

    public UiPathDistributedCacheTests()
    {
        _clock.UtcNow.Returns(Now);
        _cache = new UiPathDistributedCache(
            _inner,
            new UiPathDistributedCacheOptions(),
            policyFactory: null,
            NullLogger<UiPathDistributedCache>.Instance,
            _clock);
    }

    private static byte[] Envelope(long? slidingTicks = null, DateTimeOffset? absolute = null) =>
        new DistributedCacheEnvelope(Payload, slidingTicks, absolute).Encode();

    [Fact]
    public async Task Keys_are_case_sensitive_and_preserved()
    {
        await _cache.GetAsync("AbC-9xQ", TestContext.Current.CancellationToken);
        await _inner.Received(1).GetAsync<byte[]>(
            Arg.Is<CacheKey>(k => k.Name == "AbC-9xQ" && k.Casing == CacheKeyCasing.Sensitive),
            Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstanceName_prefixes_the_key()
    {
        var prefixed = new UiPathDistributedCache(
            _inner, new UiPathDistributedCacheOptions { InstanceName = "Sess:" },
            null, NullLogger<UiPathDistributedCache>.Instance, _clock);
        await prefixed.GetAsync("AbC", TestContext.Current.CancellationToken);
        await _inner.Received(1).GetAsync<byte[]>(
            Arg.Is<CacheKey>(k => k.Name == "Sess:AbC"),
            Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Null_key_throws()
    {
        await FluentActions.Awaiting(() => _cache.GetAsync(null!, TestContext.Current.CancellationToken)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Get_miss_returns_null()
    {
        _inner.GetAsync<byte[]>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns((byte[]?)null);
        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Get_returns_payload_and_slides_when_sliding_set()
    {
        var sliding = TimeSpan.FromMinutes(20);
        _inner.GetAsync<byte[]>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(Envelope(sliding.Ticks));

        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().Equal(Payload);

        await _inner.Received(1).RefreshAsync<byte[]>(
            Arg.Any<CacheKey>(), (DateTimeOffset?)Now.Add(sliding), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_slide_is_capped_by_absolute_expiration()
    {
        var sliding = TimeSpan.FromMinutes(20);
        var absolute = Now.AddMinutes(5);
        _inner.GetAsync<byte[]>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(Envelope(sliding.Ticks, absolute));

        await _cache.GetAsync("k", TestContext.Current.CancellationToken);

        await _inner.Received(1).RefreshAsync<byte[]>(
            Arg.Any<CacheKey>(), (DateTimeOffset?)absolute, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_without_sliding_does_not_refresh()
    {
        _inner.GetAsync<byte[]>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(Envelope(absolute: Now.AddHours(1)));

        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().Equal(Payload);

        await _inner.DidNotReceiveWithAnyArgs().RefreshAsync<byte[]>(default, (DateTimeOffset?)null, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Expired_absolute_entry_is_a_miss_and_gets_removed()
    {
        _inner.GetAsync<byte[]>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(Envelope(TimeSpan.FromMinutes(20).Ticks, Now.AddMinutes(-1)));

        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().BeNull();

        await _inner.Received(1).RemoveAsync<byte[]>(Arg.Any<CacheKey>(), Arg.Any<CancellationToken>());
        await _inner.DidNotReceiveWithAnyArgs().RefreshAsync<byte[]>(default, (DateTimeOffset?)null, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Foreign_value_is_a_miss_not_an_exception()
    {
        _inner.GetAsync<byte[]>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns("""{"legacy":"json"}"""u8.ToArray());

        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Set_with_sliding_only_uses_sliding_ttl()
    {
        var sliding = TimeSpan.FromMinutes(20);
        byte[]? stored = null;
        TimeSpan? ttl = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Do<byte[]?>(v => stored = v),
            Arg.Do<TimeSpan?>(t => ttl = t), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());

        await _cache.SetAsync("k", Payload, new DistributedCacheEntryOptions { SlidingExpiration = sliding }, TestContext.Current.CancellationToken);

        ttl.Should().Be(sliding);
        var envelope = DistributedCacheEnvelope.TryDecode(stored)!;
        envelope.SlidingTicks.Should().Be(sliding.Ticks);
        envelope.AbsoluteExpiration.Should().BeNull();
        envelope.Data.Should().Equal(Payload);
    }

    [Fact]
    public async Task Set_with_both_uses_min_of_sliding_and_remaining_absolute()
    {
        TimeSpan? ttl = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<byte[]?>(),
            Arg.Do<TimeSpan?>(t => ttl = t), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());

        await _cache.SetAsync("k", Payload, new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(20),
            AbsoluteExpiration = Now.AddMinutes(5),
        }, TestContext.Current.CancellationToken);

        ttl.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Set_with_relative_absolute_computes_from_now()
    {
        byte[]? stored = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Do<byte[]?>(v => stored = v),
            Arg.Any<TimeSpan?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());

        await _cache.SetAsync("k", Payload, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
        }, TestContext.Current.CancellationToken);

        DistributedCacheEnvelope.TryDecode(stored)!.AbsoluteExpiration.Should().Be(Now.AddMinutes(30));
    }

    [Fact]
    public async Task Set_with_no_expiration_passes_null_ttl_for_policy_default()
    {
        TimeSpan? ttl = TimeSpan.FromDays(999);
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<byte[]?>(),
            Arg.Do<TimeSpan?>(t => ttl = t), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());

        await _cache.SetAsync("k", Payload, new DistributedCacheEntryOptions(), TestContext.Current.CancellationToken);

        ttl.Should().BeNull();
    }

    [Fact]
    public async Task Set_with_past_absolute_expiration_throws()
    {
        await FluentActions.Awaiting(() => _cache.SetAsync("k", Payload,
                new DistributedCacheEntryOptions { AbsoluteExpiration = Now.AddMinutes(-1) }, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Refresh_slides_without_returning_payload()
    {
        var sliding = TimeSpan.FromMinutes(20);
        _inner.GetAsync<byte[]>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(Envelope(sliding.Ticks));

        await _cache.RefreshAsync("k", TestContext.Current.CancellationToken);

        await _inner.Received(1).RefreshAsync<byte[]>(
            Arg.Any<CacheKey>(), (DateTimeOffset?)Now.Add(sliding), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_forwards()
    {
        await _cache.RemoveAsync("AbC", TestContext.Current.CancellationToken);
        await _inner.Received(1).RemoveAsync<byte[]>(
            Arg.Is<CacheKey>(k => k.Name == "AbC"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlideByRewrite_extends_by_resetting_the_stored_bytes()
    {
        var sliding = TimeSpan.FromMinutes(20);
        var stored = Envelope(sliding.Ticks);
        var rewriting = new UiPathDistributedCache(
            _inner, new UiPathDistributedCacheOptions(),
            null, NullLogger<UiPathDistributedCache>.Instance, _clock, slideByRewrite: true);
        _inner.GetAsync<byte[]>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(stored);

        (await rewriting.GetAsync("k", TestContext.Current.CancellationToken)).Should().Equal(Payload);

        await _inner.Received(1).SetAsync(
            Arg.Any<CacheKey>(), Arg.Is<byte[]?>(v => v == stored), (TimeSpan?)sliding, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await _inner.DidNotReceiveWithAnyArgs().RefreshAsync<byte[]>(default, (DateTimeOffset?)null, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Sync_methods_block_on_async()
    {
        _inner.GetAsync<byte[]>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(Envelope());
        _cache.Get("k").Should().Equal(Payload);
    }
}
