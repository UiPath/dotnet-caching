using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests;

public class CachePolicyMergerTests
{
    [Fact]
    public void Named_wins_per_field()
    {
        var named = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };
        var defaults = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(5) };

        var merged = CachePolicyMerger.Merge(named, defaults);

        merged.LocalExpiration.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Merge_fills_null_fields_of_named_from_defaults()
    {
        var named = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };
        var defaults = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(10) };

        var merged = CachePolicyMerger.Merge(named, defaults);

        merged.LocalExpiration.Should().Be(TimeSpan.FromMinutes(1));
        merged.DistributedExpiration.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void LocalExpirationDisconnected_merges_named_over_default()
    {
        var named = new CachePolicy { LocalExpirationDisconnected = TimeSpan.FromSeconds(15) };
        var defaults = new CachePolicy { LocalExpirationDisconnected = TimeSpan.FromMinutes(2) };

        var merged = CachePolicyMerger.Merge(named, defaults);

        merged.LocalExpirationDisconnected.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void LocalExpirationDisconnected_inherits_from_default_when_named_is_null()
    {
        var named = new CachePolicy();
        var defaults = new CachePolicy { LocalExpirationDisconnected = TimeSpan.FromMinutes(2) };

        var merged = CachePolicyMerger.Merge(named, defaults);

        merged.LocalExpirationDisconnected.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Lock_merges_field_by_field_when_both_present()
    {
        var named = new CachePolicy { Lock = new LockProfile { LocalLockEnabled = false } };
        var defaults = new CachePolicy
        {
            Lock = new LockProfile { LocalLockTimeout = TimeSpan.FromMilliseconds(500), DistributedLockEnabled = true },
        };

        var merged = CachePolicyMerger.Merge(named, defaults);

        merged.Lock!.LocalLockEnabled.Should().BeFalse();
        merged.Lock.LocalLockTimeout.Should().Be(TimeSpan.FromMilliseconds(500));
        merged.Lock.DistributedLockEnabled.Should().BeTrue();
    }

    [Fact]
    public void Lock_inherits_whole_object_when_named_has_no_lock()
    {
        var named = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };
        var defaults = new CachePolicy { Lock = new LockProfile { LocalLockEnabled = false } };

        var merged = CachePolicyMerger.Merge(named, defaults);

        merged.Lock.Should().BeSameAs(defaults.Lock);
    }

    [Fact]
    public void Lock_keeps_named_when_default_has_no_lock()
    {
        var named = new CachePolicy { Lock = new LockProfile { LocalLockEnabled = false } };
        var defaults = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(5) };

        var merged = CachePolicyMerger.Merge(named, defaults);

        merged.Lock.Should().BeSameAs(named.Lock);
    }

    [Fact]
    public void Rehydrate_is_not_recursively_merged()
    {
        var named = new CachePolicy { Rehydrate = new RehydrateOptions { Threshold = 0.6 } };
        var defaults = new CachePolicy { Rehydrate = new RehydrateOptions { Threshold = 0.8, Name = "default" } };

        var merged = CachePolicyMerger.Merge(named, defaults);

        merged.Rehydrate.Should().BeSameAs(named.Rehydrate, "rehydrate is all-or-nothing per name; no field-level merge");
    }

    [Fact]
    public void RehydrateEnabled_matched_wins_over_default()
    {
        var named = new CachePolicy { RehydrateEnabled = true };
        var defaults = new CachePolicy { RehydrateEnabled = false };

        CachePolicyMerger.Merge(named, defaults).RehydrateEnabled.Should().BeTrue();
    }

    [Fact]
    public void RehydrateEnabled_inherits_from_default_when_named_null()
    {
        var named = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };
        var defaults = new CachePolicy { RehydrateEnabled = true };

        CachePolicyMerger.Merge(named, defaults).RehydrateEnabled.Should().BeTrue();
    }

    [Fact]
    public void Merging_two_empty_returns_empty_fields()
    {
        var merged = CachePolicyMerger.Merge(new CachePolicy(), new CachePolicy());

        merged.LocalExpiration.Should().BeNull();
        merged.DistributedExpiration.Should().BeNull();
        merged.FactoryTimeout.Should().BeNull();
        merged.JitterMaxDuration.Should().BeNull();
        merged.RehydrateEnabled.Should().BeNull();
        merged.Lock.Should().BeNull();
        merged.Rehydrate.Should().BeNull();
    }

    [Fact]
    public void JitterMaxDuration_named_wins_over_default()
    {
        var named = new CachePolicy { JitterMaxDuration = TimeSpan.FromSeconds(10) };
        var defaults = new CachePolicy { JitterMaxDuration = TimeSpan.FromMinutes(1) };

        CachePolicyMerger.Merge(named, defaults).JitterMaxDuration.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void JitterMaxDuration_inherits_from_default_when_named_is_null()
    {
        var named = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };
        var defaults = new CachePolicy { JitterMaxDuration = TimeSpan.FromSeconds(30) };

        CachePolicyMerger.Merge(named, defaults).JitterMaxDuration.Should().Be(TimeSpan.FromSeconds(30));
    }
}
