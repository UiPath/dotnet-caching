namespace UiPath.Platform.Caching.Tests;

public class CachePolicyTests
{
    [Fact]
    public void Empty_singleton_has_all_null_fields()
    {
        var empty = CachePolicy.Empty;

        empty.LocalExpiration.Should().BeNull();
        empty.DistributedExpiration.Should().BeNull();
        empty.FactoryTimeout.Should().BeNull();
        empty.RehydrateEnabled.Should().BeNull();
        empty.Rehydrate.Should().BeNull();
        empty.Lock.Should().BeNull();
    }

    [Fact]
    public void Default_constructed_has_all_null_fields()
    {
        var policy = new CachePolicy();

        policy.LocalExpiration.Should().BeNull();
        policy.DistributedExpiration.Should().BeNull();
        policy.FactoryTimeout.Should().BeNull();
        policy.RehydrateEnabled.Should().BeNull();
        policy.Rehydrate.Should().BeNull();
        policy.Lock.Should().BeNull();
    }

    [Fact]
    public void LockProfile_default_constructed_has_all_null_fields()
    {
        var profile = new LockProfile();

        profile.LocalLockEnabled.Should().BeNull();
        profile.DistributedLockEnabled.Should().BeNull();
        profile.LocalLockTimeout.Should().BeNull();
        profile.DistributedLockTimeout.Should().BeNull();
        profile.DistributedLockExpiry.Should().BeNull();
    }

    [Fact]
    public void Fields_carry_init_values()
    {
        var policy = new CachePolicy
        {
            LocalExpiration = TimeSpan.FromMinutes(1),
            DistributedExpiration = TimeSpan.FromMinutes(5),
            FactoryTimeout = TimeSpan.FromSeconds(2),
            RehydrateEnabled = true,
            Lock = new LockProfile { LocalLockEnabled = false },
        };

        policy.LocalExpiration.Should().Be(TimeSpan.FromMinutes(1));
        policy.DistributedExpiration.Should().Be(TimeSpan.FromMinutes(5));
        policy.FactoryTimeout.Should().Be(TimeSpan.FromSeconds(2));
        policy.RehydrateEnabled.Should().BeTrue();
        policy.Lock!.LocalLockEnabled.Should().BeFalse();
    }
}
