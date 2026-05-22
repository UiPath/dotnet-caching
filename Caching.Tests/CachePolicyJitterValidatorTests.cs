using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests;

public class CachePolicyJitterValidatorTests
{
    private static readonly CachePolicyJitterValidator Sut = new();

    [Fact]
    public void Succeeds_when_no_policies_or_default()
    {
        Sut.Validate(name: null, new CacheOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Succeeds_when_Policies_is_null()
    {
        var options = new CacheOptions { Policies = null! };

        Sut.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Succeeds_when_JitterMaxDuration_is_zero()
    {
        var options = new CacheOptions
        {
            DefaultCachePolicy = new CachePolicy { JitterMaxDuration = TimeSpan.Zero },
        };

        Sut.Validate(name: null, options).Succeeded.Should().BeTrue("zero is a no-op, not an invalid value");
    }

    [Fact]
    public void Succeeds_when_JitterMaxDuration_is_positive()
    {
        var options = new CacheOptions
        {
            DefaultCachePolicy = new CachePolicy { JitterMaxDuration = TimeSpan.FromSeconds(30) },
        };

        Sut.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Fails_when_DefaultCachePolicy_JitterMaxDuration_is_negative()
    {
        var options = new CacheOptions
        {
            DefaultCachePolicy = new CachePolicy { JitterMaxDuration = TimeSpan.FromSeconds(-1) },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle(f => f.StartsWith("DefaultCachePolicy.JitterMaxDuration must be"));
    }

    [Fact]
    public void Fails_when_named_policy_JitterMaxDuration_is_negative()
    {
        var options = new CacheOptions
        {
            Policies = new Dictionary<string, CachePolicy>
            {
                ["clients"] = new CachePolicy { JitterMaxDuration = TimeSpan.FromSeconds(-5) },
            },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle(f => f.StartsWith("Policies['clients'].JitterMaxDuration must be"));
    }
}
