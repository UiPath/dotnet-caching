using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests.Locking;

public class CacheOptionsLockValidatorTests
{
    private static CacheOptions Valid() => new()
    {
        LocalLockPoolSize = 100,
        LocalLockPoolInitialFill = 10,
        DistributedLockPollInterval = TimeSpan.FromMilliseconds(50),
        DistributedLockMaxPollInterval = TimeSpan.FromMilliseconds(500),
    };

    [Fact]
    public void Succeeds_for_valid_options()
    {
        var sut = new CacheOptionsLockValidator();
        var result = sut.Validate(name: null, Valid());
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Fails_when_LocalLockPoolSize_is_zero()
    {
        var opts = Valid();
        opts.LocalLockPoolSize = 0;
        var sut = new CacheOptionsLockValidator();
        var result = sut.Validate(name: null, opts);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(CacheOptions.LocalLockPoolSize));
    }

    [Fact]
    public void Fails_when_LocalLockPoolSize_is_negative()
    {
        var opts = Valid();
        opts.LocalLockPoolSize = -1;
        var sut = new CacheOptionsLockValidator();
        var result = sut.Validate(name: null, opts);
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Fails_when_LocalLockPoolInitialFill_is_negative()
    {
        var opts = Valid();
        opts.LocalLockPoolInitialFill = -1;
        var sut = new CacheOptionsLockValidator();
        var result = sut.Validate(name: null, opts);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(CacheOptions.LocalLockPoolInitialFill));
    }

    [Fact]
    public void Fails_when_LocalLockPoolInitialFill_exceeds_LocalLockPoolSize()
    {
        var opts = Valid();
        opts.LocalLockPoolSize = 10;
        opts.LocalLockPoolInitialFill = 11;
        var sut = new CacheOptionsLockValidator();
        var result = sut.Validate(name: null, opts);
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Fails_when_DistributedLockPollInterval_is_zero()
    {
        var opts = Valid();
        opts.DistributedLockPollInterval = TimeSpan.Zero;
        var sut = new CacheOptionsLockValidator();
        var result = sut.Validate(name: null, opts);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(CacheOptions.DistributedLockPollInterval));
    }

    [Fact]
    public void Fails_when_DistributedLockMaxPollInterval_is_less_than_DistributedLockPollInterval()
    {
        var opts = Valid();
        opts.DistributedLockPollInterval = TimeSpan.FromMilliseconds(100);
        opts.DistributedLockMaxPollInterval = TimeSpan.FromMilliseconds(50);
        var sut = new CacheOptionsLockValidator();
        var result = sut.Validate(name: null, opts);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(CacheOptions.DistributedLockMaxPollInterval));
    }
}
