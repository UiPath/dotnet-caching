using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests;

public class CachePolicyLockValidatorTests
{
    private static readonly CachePolicyLockValidator Sut = new();

    [Fact]
    public void Succeeds_when_no_policies_or_default_lock()
    {
        Sut.Validate(name: null, new CacheOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Succeeds_for_well_formed_named_policy_Lock()
    {
        var options = new CacheOptions
        {
            Policies =
            {
                ["clients-cache"] = new CachePolicy
                {
                    Lock = new LockProfile
                    {
                        LocalLockEnabled = true,
                        DistributedLockEnabled = true,
                        LocalLockTimeout = TimeSpan.FromMilliseconds(250),
                        DistributedLockTimeout = TimeSpan.FromMilliseconds(500),
                        DistributedLockExpiry = TimeSpan.FromSeconds(5),
                    },
                },
            },
        };

        Sut.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Fails_when_named_policy_Lock_has_zero_DistributedLockExpiry()
    {
        var options = new CacheOptions
        {
            Policies =
            {
                ["bad-cache"] = new CachePolicy { Lock = new LockProfile { DistributedLockExpiry = TimeSpan.Zero } },
            },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("bad-cache").And.Contain(nameof(LockProfile.DistributedLockExpiry));
    }

    [Fact]
    public void Fails_when_DefaultCachePolicy_Lock_has_LocalLockEnabled_false_with_DistributedLockEnabled_true()
    {
        var options = new CacheOptions
        {
            DefaultCachePolicy = new CachePolicy
            {
                Lock = new LockProfile { LocalLockEnabled = false, DistributedLockEnabled = true },
            },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("DefaultCachePolicy");
    }

    [Fact]
    public void Fails_when_named_policy_Lock_LocalLockTimeout_is_zero()
    {
        var options = new CacheOptions
        {
            Policies =
            {
                ["x"] = new CachePolicy { Lock = new LockProfile { LocalLockTimeout = TimeSpan.Zero } },
            },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("x").And.Contain(nameof(LockProfile.LocalLockTimeout));
    }

    [Fact]
    public void Fails_when_DistributedLockPollInterval_exceeds_named_DistributedLockTimeout()
    {
        var options = new CacheOptions
        {
            DistributedLockPollInterval = TimeSpan.FromMilliseconds(600),
            Policies =
            {
                ["x"] = new CachePolicy { Lock = new LockProfile { DistributedLockTimeout = TimeSpan.FromMilliseconds(400) } },
            },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("DistributedLockPollInterval");
    }

    [Fact]
    public void Skips_named_policies_without_Lock_set()
    {
        var options = new CacheOptions
        {
            Policies =
            {
                ["x"] = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) },
            },
        };

        Sut.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Fails_when_merged_default_plus_named_produces_invalid_combo()
    {
        // Default contributes LocalLockEnabled=false; named adds DistributedLockEnabled=true.
        // Merged profile = LocalLockEnabled=false + DistributedLockEnabled=true, which is invalid
        // even though each sparse half is fine on its own.
        var options = new CacheOptions
        {
            DefaultCachePolicy = new CachePolicy
            {
                Lock = new LockProfile { LocalLockEnabled = false },
            },
            Policies =
            {
                ["named"] = new CachePolicy { Lock = new LockProfile { DistributedLockEnabled = true } },
            },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue("validator must check the merged profile so cross-policy invariants don't escape to RunUnderLocksAsync");
        result.FailureMessage.Should().Contain("named").And.Contain(nameof(LockProfile.LocalLockEnabled));
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
    public void ValidateNamedPoliciesAgainstDefaultLock_treats_null_Policies_as_empty()
    {
        var options = new CacheOptions { Policies = null! };
        var builderDefaultLock = new LockProfile { LocalLockEnabled = false, DistributedLockEnabled = true };

        var act = () => CachePolicyLockValidator.ValidateNamedPoliciesAgainstDefaultLock(options, builderDefaultLock);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateNamedPoliciesAgainstDefaultLock_throws_when_merged_combo_is_invalid()
    {
        // Builder-derived default lock has LocalLockEnabled=false; named policy flips
        // DistributedLockEnabled=true. Merged = invalid combo. This is the case that the
        // standard IValidateOptions path can't reach because injecting ICachePolicyDefaultBuilder
        // would form a DI cycle through MultilayerCacheLockCrossOptionsValidator → IOptions<CacheOptions>.
        var options = new CacheOptions
        {
            Policies =
            {
                ["named"] = new CachePolicy { Lock = new LockProfile { DistributedLockEnabled = true } },
            },
        };
        var builderDefaultLock = new LockProfile { LocalLockEnabled = false };

        var act = () => CachePolicyLockValidator.ValidateNamedPoliciesAgainstDefaultLock(options, builderDefaultLock);

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("named") && f.Contains(nameof(LockProfile.LocalLockEnabled)));
    }

    [Fact]
    public void ValidateNamedPoliciesAgainstDefaultLock_skips_named_policies_with_no_lock_overrides()
    {
        // A named policy that inherits the lock profile verbatim (Lock == null) doesn't introduce
        // any new combos beyond what the default itself already encodes — that default was already
        // self-validated, so there's nothing left to check.
        var options = new CacheOptions
        {
            Policies =
            {
                ["inherits"] = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(1) },
            },
        };
        var builderDefaultLock = new LockProfile { LocalLockEnabled = false, DistributedLockEnabled = true };

        var act = () => CachePolicyLockValidator.ValidateNamedPoliciesAgainstDefaultLock(options, builderDefaultLock);

        // Even though the default lock itself is invalid, this helper only validates merged
        // overrides — the default's own validation belongs to the provider that built it.
        act.Should().NotThrow();
    }

    [Fact]
    public void Validates_DefaultCachePolicy_and_named_policies()
    {
        var options = new CacheOptions
        {
            DefaultCachePolicy = new CachePolicy { Lock = new LockProfile { LocalLockTimeout = TimeSpan.FromMilliseconds(100) } },
            Policies =
            {
                ["good"] = new CachePolicy { Lock = new LockProfile { LocalLockTimeout = TimeSpan.FromMilliseconds(100) } },
                ["bad"] = new CachePolicy { Lock = new LockProfile { DistributedLockExpiry = TimeSpan.Zero } },
            },
        };

        var result = Sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("bad");
    }
}
