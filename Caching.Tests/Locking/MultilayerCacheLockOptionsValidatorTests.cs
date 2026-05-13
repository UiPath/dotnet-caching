using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests.Locking;

public class MultilayerCacheLockOptionsValidatorTests
{
    [Fact]
    public void Succeeds_when_lock_options_are_unset()
    {
        var sut = new MultilayerCacheLockOptionsValidator<InMemoryRedisCacheOptions>();
        var result = sut.Validate(name: null, new InMemoryRedisCacheOptions
        {
            DistributedLockExpiry = null,
            DistributedLockTimeout = null,
        });
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Succeeds_for_positive_expiry_and_non_negative_timeout()
    {
        var sut = new MultilayerCacheLockOptionsValidator<InMemoryRedisCacheOptions>();
        var result = sut.Validate(name: null, new InMemoryRedisCacheOptions
        {
            DistributedLockExpiry = TimeSpan.FromSeconds(1),
            DistributedLockTimeout = TimeSpan.Zero,
        });
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Fails_when_DistributedLockExpiry_is_zero()
    {
        var sut = new MultilayerCacheLockOptionsValidator<InMemoryRedisCacheOptions>();
        var result = sut.Validate(name: null, new InMemoryRedisCacheOptions
        {
            DistributedLockExpiry = TimeSpan.Zero,
        });
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(IMultilayerCacheOptions.DistributedLockExpiry));
    }

    [Fact]
    public void Fails_when_DistributedLockExpiry_is_negative()
    {
        var sut = new MultilayerCacheLockOptionsValidator<InMemoryRedisCacheOptions>();
        var result = sut.Validate(name: null, new InMemoryRedisCacheOptions
        {
            DistributedLockExpiry = TimeSpan.FromMilliseconds(-1),
        });
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Fails_when_DistributedLockTimeout_is_negative()
    {
        var sut = new MultilayerCacheLockOptionsValidator<InMemoryRedisCacheOptions>();
        var result = sut.Validate(name: null, new InMemoryRedisCacheOptions
        {
            DistributedLockTimeout = TimeSpan.FromMilliseconds(-1),
        });
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(IMultilayerCacheOptions.DistributedLockTimeout));
    }
}
