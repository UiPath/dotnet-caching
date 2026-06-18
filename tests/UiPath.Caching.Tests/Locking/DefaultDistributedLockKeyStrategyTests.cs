using UiPath.Caching.Locking;

namespace UiPath.Caching.Tests.Locking;

public class DefaultDistributedLockKeyStrategyTests
{
    [Theory]
    [InlineData(':', "my-key", "my-key:lck")]
    [InlineData('$', "my-key", "my-key$lck")]
    [InlineData(':', "tenant:cache:key", "tenant:cache:key:lck")]
    public void GetLockKey_appends_lck_with_configured_separator(char separator, string input, string expected)
    {
        var sut = new DefaultDistributedLockKeyStrategy(separator);
        var result = sut.GetLockKey((CacheKey)input);
        result.Should().Be(expected);
    }
}
