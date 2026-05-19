using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests;

public class CachePolicyRehydrateValidatorTests
{
    private static readonly CachePolicyRehydrateValidator Sut = new();

    private static RehydrateOptions ValidRehydrate(
        double threshold = 0.75,
        double timeoutFraction = 0.5,
        TimeSpan? baseCooldown = null,
        TimeSpan? maxCooldown = null) => new()
    {
        Threshold = threshold,
        TimeoutFraction = timeoutFraction,
        BaseCooldown = baseCooldown ?? TimeSpan.FromSeconds(1),
        MaxCooldown = maxCooldown ?? TimeSpan.FromMinutes(5),
    };

    [Fact]
    public void Succeeds_when_no_policies_or_default_Rehydrate()
    {
        Sut.Validate(name: null, new CacheOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Succeeds_when_Policies_is_null()
    {
        // CacheOptions.Policies has a public setter — configuration binding can leave it null.
        // The validator must treat null as empty, not NRE at startup.
        var options = new CacheOptions { Policies = null! };

        Sut.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Succeeds_for_well_formed_named_policy_Rehydrate()
    {
        var options = new CacheOptions
        {
            Policies = { ["clients-cache"] = new CachePolicy { Rehydrate = ValidRehydrate() } },
        };

        Sut.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Succeeds_for_well_formed_DefaultCachePolicy_Rehydrate()
    {
        var options = new CacheOptions { DefaultCachePolicy = new CachePolicy { Rehydrate = ValidRehydrate() } };

        Sut.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void Fails_when_named_policy_Threshold_is_out_of_range(double threshold)
    {
        var options = new CacheOptions
        {
            Policies = { ["bad-cache"] = new CachePolicy { Rehydrate = ValidRehydrate(threshold: threshold) } },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("bad-cache").And.Contain(nameof(RehydrateOptions.Threshold));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(2.0)]
    public void Fails_when_TimeoutFraction_is_out_of_range(double timeoutFraction)
    {
        var options = new CacheOptions
        {
            DefaultCachePolicy = new CachePolicy { Rehydrate = ValidRehydrate(timeoutFraction: timeoutFraction) },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("DefaultCachePolicy").And.Contain(nameof(RehydrateOptions.TimeoutFraction));
    }

    [Fact]
    public void Fails_when_BaseCooldown_is_zero()
    {
        var options = new CacheOptions
        {
            Policies = { ["bad-cache"] = new CachePolicy { Rehydrate = ValidRehydrate(baseCooldown: TimeSpan.Zero) } },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(RehydrateOptions.BaseCooldown));
    }

    [Fact]
    public void Fails_when_MaxCooldown_is_negative()
    {
        var options = new CacheOptions
        {
            Policies = { ["bad-cache"] = new CachePolicy { Rehydrate = ValidRehydrate(maxCooldown: TimeSpan.FromSeconds(-1)) } },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(RehydrateOptions.MaxCooldown));
    }

    [Fact]
    public void Fails_when_MaxCooldown_is_less_than_BaseCooldown()
    {
        var options = new CacheOptions
        {
            Policies =
            {
                ["bad-cache"] = new CachePolicy
                {
                    Rehydrate = ValidRehydrate(
                        baseCooldown: TimeSpan.FromMinutes(5),
                        maxCooldown: TimeSpan.FromSeconds(30)),
                },
            },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(RehydrateOptions.MaxCooldown)).And.Contain(nameof(RehydrateOptions.BaseCooldown));
    }

    [Fact]
    public void Fails_when_DefaultCachePolicy_Rehydrate_is_invalid_short_circuits_before_named()
    {
        var options = new CacheOptions
        {
            DefaultCachePolicy = new CachePolicy { Rehydrate = ValidRehydrate(threshold: 0) },
            Policies = { ["clients-cache"] = new CachePolicy { Rehydrate = ValidRehydrate() } },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("DefaultCachePolicy");
    }
}
