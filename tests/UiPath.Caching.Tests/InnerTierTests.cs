namespace UiPath.Caching.Tests;

public sealed class InnerTierTests
{
    [Fact]
    public void No_tier_is_not_shared_and_reports_no_capability()
    {
        var sut = InnerTier.None;

        sut.Should().BeOfType<NoInnerTier>();
        sut.IsShared.Should().BeFalse();
        sut.Invalidations.Should().BeNull();
        sut.Versioned.Should().BeNull();
        sut.Connection.Should().BeNull();
    }

    [Fact]
    public void The_null_caches_are_no_tier()
    {
        InnerTier.For(NullCache.Instance).Should().BeSameAs(InnerTier.None);
        InnerTier.For(NullHashCache.Instance).Should().BeSameAs(InnerTier.None);
    }

    [Fact]
    public void A_shared_tier_is_shared_and_exposes_the_capabilities_the_cache_implements()
    {
        var cache = FullFeatured();
        var options = new InMemoryRedisCacheOptions { PeerFieldUpdates = true, PeerFieldUpdateMaxFields = 7, PeerFieldUpdateMaxBytes = 99 };

        var sut = InnerTier.For(cache, options);

        sut.Should().BeOfType<SharedInnerTier>();
        sut.IsShared.Should().BeTrue();
        sut.Invalidations.Should().BeSameAs(cache);
        sut.Versioned.Should().BeSameAs(cache);
        sut.Connection.Should().BeSameAs(cache);
        sut.PeerLimits.Should().Be(new PeerFieldLimits(7, 99));
    }

    [Fact]
    public void A_shared_tier_without_capabilities_reports_none()
    {
        var sut = InnerTier.For(Substitute.For<IHashCache>(), new InMemoryRedisCacheOptions { PeerFieldUpdates = true });

        sut.IsShared.Should().BeTrue();
        sut.Invalidations.Should().BeNull();
        sut.Versioned.Should().BeNull();
        sut.Connection.Should().BeNull();
    }

    [Fact]
    public void Versioned_writes_need_peer_field_updates_switched_on()
    {
        var cache = FullFeatured();

        InnerTier.For(cache, new InMemoryRedisCacheOptions { PeerFieldUpdates = false }).Versioned.Should().BeNull();
        InnerTier.For(cache).Versioned.Should().BeNull();
    }

    private static FullFeaturedTier FullFeatured() => new();

    private sealed class FullFeaturedTier : IInvalidationSink, IVersionedHashCache, IConnectionState
    {
        public event EventHandler? OnConnectionFailed
        {
            add { }
            remove { }
        }

        public event EventHandler? OnConnectionRestored
        {
            add { }
            remove { }
        }

        public event EventHandler? OnReconnected
        {
            add { }
            remove { }
        }

        public bool IsConnected => true;

        public void Invalidated(CacheKey key)
        {
        }

        public void InvalidatedAll()
        {
        }

        public ValueTask<VersionedWrite> SetVersionedAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, PeerFieldLimits limits, CancellationToken token = default) =>
            throw new NotSupportedException();

        public ValueTask<bool> RemoveFieldsVersionedAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
            throw new NotSupportedException();

        public bool TryDecodeFields<T>(IDictionary<string, string?> encoded, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out Dictionary<string, T?> values) =>
            throw new NotSupportedException();
    }
}
