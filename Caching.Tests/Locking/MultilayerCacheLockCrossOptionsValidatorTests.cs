using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests.Locking;

public class MultilayerCacheLockCrossOptionsValidatorTests
{
    private static MultilayerCacheLockCrossOptionsValidator<InMemoryRedisCacheOptions> NewSut(CacheOptions? cacheOptions = null) =>
        new(Options.Create(cacheOptions ?? new CacheOptions()));

    [Fact]
    public void Succeeds_for_default_options()
    {
        var sut = NewSut();
        var result = sut.Validate(name: null, new InMemoryRedisCacheOptions());
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Succeeds_when_DistributedLockPollInterval_equals_DistributedLockTimeout()
    {
        var sut = NewSut(new CacheOptions
        {
            DistributedLockPollInterval = TimeSpan.FromMilliseconds(500),
            DistributedLockMaxPollInterval = TimeSpan.FromMilliseconds(500),
        });
        var result = sut.Validate(name: null, new InMemoryRedisCacheOptions
        {
            DistributedLockTimeout = TimeSpan.FromMilliseconds(500),
        });
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Fails_when_DistributedLockPollInterval_exceeds_DistributedLockTimeout()
    {
        var sut = NewSut(new CacheOptions
        {
            DistributedLockPollInterval = TimeSpan.FromMilliseconds(600),
            DistributedLockMaxPollInterval = TimeSpan.FromSeconds(1),
        });
        var result = sut.Validate(name: null, new InMemoryRedisCacheOptions
        {
            DistributedLockTimeout = TimeSpan.FromMilliseconds(500),
        });
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(CacheOptions.DistributedLockPollInterval));
        result.FailureMessage.Should().Contain(nameof(IMultilayerCacheOptions.DistributedLockTimeout));
    }

    [Fact]
    public void Fails_when_DistributedLockPollInterval_exceeds_default_DistributedLockTimeout()
    {
        var sut = NewSut(new CacheOptions
        {
            DistributedLockPollInterval = TimeSpan.FromMilliseconds(600),
            DistributedLockMaxPollInterval = TimeSpan.FromSeconds(1),
        });
        var result = sut.Validate(name: null, new InMemoryRedisCacheOptions());
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Succeeds_when_DistributedLockMaxPollInterval_exceeds_DistributedLockTimeout()
    {
        var sut = NewSut(new CacheOptions
        {
            DistributedLockPollInterval = TimeSpan.FromMilliseconds(50),
            DistributedLockMaxPollInterval = TimeSpan.FromSeconds(2),
        });
        var result = sut.Validate(name: null, new InMemoryRedisCacheOptions
        {
            DistributedLockTimeout = TimeSpan.FromMilliseconds(500),
        });
        result.Succeeded.Should().BeTrue();
    }
}
