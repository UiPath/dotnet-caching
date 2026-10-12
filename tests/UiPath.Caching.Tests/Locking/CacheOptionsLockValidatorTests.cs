using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Locking;

public class CacheOptionsLockValidatorTests
{

    [Fact]
    public void Succeeds_for_valid_options()
    {
        var sut = new CacheOptionsLockValidator();
        var result = sut.Validate(name: null, Valid());
        result.Succeeded.Should().BeTrue();
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
    private static CacheOptions Valid() => new()
    {
        DistributedLockPollInterval = TimeSpan.FromMilliseconds(50),
        DistributedLockMaxPollInterval = TimeSpan.FromMilliseconds(500),
    };
}
