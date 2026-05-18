using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Platform.Caching.Config;
using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Caching.Tests;

public class PropagateCacheNullValuesFromMultilayerTests
{
    private static PropagateCacheNullValuesFromMultilayer Create(bool sourceCacheNullValues)
    {
        var source = Options.Create(new InMemoryRedisCacheOptions { CacheNullValues = sourceCacheNullValues });
        return new PropagateCacheNullValuesFromMultilayer(source, NullLoggerFactory.Instance);
    }

    [Fact]
    public void PostConfigure_forces_target_on_when_source_on()
    {
        var target = new RedisCacheOptions();
        Create(sourceCacheNullValues: true).PostConfigure(Options.DefaultName, target);

        target.CacheNullValues.Should().BeTrue();
    }

    [Fact]
    public void PostConfigure_noop_when_source_off()
    {
        var target = new RedisCacheOptions();
        Create(sourceCacheNullValues: false).PostConfigure(Options.DefaultName, target);

        target.CacheNullValues.Should().BeFalse();
    }

    [Fact]
    public void PostConfigure_noop_when_target_already_on()
    {
        var target = new RedisCacheOptions { CacheNullValues = true };
        Create(sourceCacheNullValues: true).PostConfigure(Options.DefaultName, target);

        target.CacheNullValues.Should().BeTrue();
    }

    [Theory]
    [InlineData("other")]
    [InlineData("named-instance")]
    public void PostConfigure_ignores_non_default_named_options(string namedKey)
    {
        var target = new RedisCacheOptions();
        Create(sourceCacheNullValues: true).PostConfigure(namedKey, target);

        target.CacheNullValues.Should().BeFalse();
    }
}
