using UiPath.Caching.Config;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

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
        var named = new CachePolicy { Lock = new LockProfile { LocalSingleFlightEnabled = false } };
        var defaults = new CachePolicy
        {
            Lock = new LockProfile { LocalSingleFlightTimeout = TimeSpan.FromMilliseconds(500), DistributedLockEnabled = true },
        };

        var merged = CachePolicyMerger.Merge(named, defaults);

        merged.Lock!.LocalSingleFlightEnabled.Should().BeFalse();
        merged.Lock.LocalSingleFlightTimeout.Should().Be(TimeSpan.FromMilliseconds(500));
        merged.Lock.DistributedLockEnabled.Should().BeTrue();
    }

    [Fact]
    public void Lock_inherits_whole_object_when_named_has_no_lock()
    {
        var named = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };
        var defaults = new CachePolicy { Lock = new LockProfile { LocalSingleFlightEnabled = false } };

        var merged = CachePolicyMerger.Merge(named, defaults);

        merged.Lock.Should().BeSameAs(defaults.Lock);
    }

    [Fact]
    public void Lock_keeps_named_when_default_has_no_lock()
    {
        var named = new CachePolicy { Lock = new LockProfile { LocalSingleFlightEnabled = false } };
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
    public void RefreshLocalOnPeerWrite_matched_wins_over_default()
    {
        var named = new CachePolicy { RefreshLocalOnPeerWrite = false };
        var defaults = new CachePolicy { RefreshLocalOnPeerWrite = true };
        CachePolicyMerger.Merge(named, defaults).RefreshLocalOnPeerWrite.Should().BeFalse();
    }

    [Fact]
    public void RefreshLocalOnPeerWrite_inherits_from_default_when_named_null()
    {
        var named = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };
        var defaults = new CachePolicy { RefreshLocalOnPeerWrite = false };
        CachePolicyMerger.Merge(named, defaults).RefreshLocalOnPeerWrite.Should().BeFalse();
    }

    [Fact]
    public void RefreshLocalRetryWindow_matched_wins_over_default_and_inherits_when_null()
    {
        var defaults = new CachePolicy { RefreshLocalRetryWindow = TimeSpan.FromSeconds(1) };
        CachePolicyMerger.Merge(new CachePolicy { RefreshLocalRetryWindow = TimeSpan.Zero }, defaults).RefreshLocalRetryWindow.Should().Be(TimeSpan.Zero);
        CachePolicyMerger.Merge(new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) }, defaults).RefreshLocalRetryWindow.Should().Be(TimeSpan.FromSeconds(1));
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
    public void Merge_returns_null_when_both_inputs_are_null()
    {
        CachePolicyMerger.Merge(null, null).Should().BeNull();
    }

    [Fact]
    public void Merge_returns_primary_when_fallback_is_null()
    {
        var primary = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };

        CachePolicyMerger.Merge(primary, null).Should().BeSameAs(primary);
    }

    [Fact]
    public void Merge_returns_fallback_when_primary_is_null()
    {
        var fallback = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(5) };

        CachePolicyMerger.Merge(null, fallback).Should().BeSameAs(fallback);
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

    [Fact]
    public void Serializer_merges_per_field()
    {
        var named = new CachePolicy { Serializer = new SerializerPolicy { Write = "a" } };
        var defaults = new CachePolicy { Serializer = new SerializerPolicy { Read = ["x"], MigrateOnRead = true } };

        var merged = CachePolicyMerger.Merge(named, defaults)!.Serializer!;

        merged.Write.Should().Be("a");
        merged.Read.Should().Equal("x");
        merged.MigrateOnRead.Should().BeTrue();
    }

    [Fact]
    public void Serializer_fields_of_the_named_policy_win_over_the_default()
    {
        var named = new CachePolicy { Serializer = new SerializerPolicy { Write = "json", Read = [], MigrateOnRead = false } };
        var defaults = new CachePolicy { Serializer = new SerializerPolicy { Write = "messagepack", Read = ["x"], MigrateOnRead = true } };

        var merged = CachePolicyMerger.Merge(named, defaults)!.Serializer!;

        merged.Write.Should().Be("json");
        merged.Read.Should().BeEmpty();
        merged.MigrateOnRead.Should().BeFalse();
    }

    [Fact]
    public void Serializer_absent_on_one_side_comes_from_the_other()
    {
        var serializer = new SerializerPolicy { Write = "a" };

        CachePolicyMerger.Merge(new CachePolicy { Serializer = serializer }, new CachePolicy())!.Serializer.Should().BeSameAs(serializer);
        CachePolicyMerger.Merge(new CachePolicy(), new CachePolicy { Serializer = serializer })!.Serializer.Should().BeSameAs(serializer);
        CachePolicyMerger.Merge(new CachePolicy(), new CachePolicy())!.Serializer.Should().BeNull();
    }

    [Fact]
    public void The_adapters_factory_drops_a_resolved_slot()
    {
        var resolved = new FakeValueSerializer(0x10);
        var source = CachePolicyMerger.WithWriteSerializer(new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } }, resolved);
        var factory = Substitute.For<ICachePolicyFactory>();
        factory.Resolve("orders").Returns(source);
        factory.Default.Returns(source);

        var sut = new AdapterPolicyFactory(factory);

        CachePolicyMerger.WriteSerializerOf(sut.Resolve("orders")!).Should().BeNull();
        CachePolicyMerger.WriteSerializerOf(sut.Default!).Should().BeNull();
        sut.Resolve("orders")!.Serializer!.Write.Should().Be("json");
        sut.Resolve("orders")!.BackgroundDistributedWrite.Should().BeFalse();
    }

    [Fact]
    public void Slot_comes_from_the_side_that_supplies_the_write_name()
    {
        var slot = new FakeValueSerializer(0x10);
        var resolvedDefault = CachePolicyMerger.WithWriteSerializer(new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } }, slot);

        // Names no write: the default's name and slot both apply.
        CachePolicyMerger.WriteSerializerOf(CachePolicyMerger.Merge(new CachePolicy(), resolvedDefault)!).Should().BeSameAs(slot);
        CachePolicyMerger.WriteSerializerOf(CachePolicyMerger.Merge(new CachePolicy { Serializer = new SerializerPolicy { Read = ["json"] } }, resolvedDefault)!).Should().BeSameAs(slot);
        // Names its own write, still unresolved: the default's slot belongs to another name.
        CachePolicyMerger.WriteSerializerOf(CachePolicyMerger.Merge(new CachePolicy { Serializer = new SerializerPolicy { Write = "other" } }, resolvedDefault)!).Should().BeNull();
        // Names json: the default's slot must not leak back in.
        CachePolicyMerger.WriteSerializerOf(CachePolicyMerger.Merge(new CachePolicy { Serializer = new SerializerPolicy { Write = "json" } }, resolvedDefault)!).Should().BeNull();
    }
}
