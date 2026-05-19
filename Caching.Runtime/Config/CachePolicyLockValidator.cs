namespace UiPath.Platform.Caching.Config;

internal sealed class CachePolicyLockValidator : IValidateOptions<CacheOptions>
{
    public ValidateOptionsResult Validate(string? name, CacheOptions options)
    {
        var pollInterval = options.DistributedLockPollInterval;
        var defaultLock = options.DefaultCachePolicy?.Lock;

        if (defaultLock is not null)
        {
            var single = ValidateLockProfile("DefaultCachePolicy.Lock: ", defaultLock);
            if (single.Failed)
            {
                return single;
            }
            var cross = LockSettingsValidator.ValidateCross("DefaultCachePolicy.Lock: ", pollInterval, defaultLock.DistributedLockTimeout);
            if (cross.Failed)
            {
                return cross;
            }
        }

        var policies = (IEnumerable<KeyValuePair<string, CachePolicy>>?)options.Policies ?? Array.Empty<KeyValuePair<string, CachePolicy>>();
        foreach (var (policyName, policy) in policies)
        {
            if (policy.Lock is null && defaultLock is null)
            {
                continue;
            }
            var merged = CachePolicyMerger.MergeLock(policy.Lock, defaultLock)!;
            var scope = $"Policies['{policyName}'].Lock: ";
            var single = ValidateLockProfile(scope, merged);
            if (single.Failed)
            {
                return single;
            }
            var cross = LockSettingsValidator.ValidateCross(scope, pollInterval, merged.DistributedLockTimeout);
            if (cross.Failed)
            {
                return cross;
            }
        }
        return ValidateOptionsResult.Success;
    }

    private static ValidateOptionsResult ValidateLockProfile(string scope, LockProfile profile) =>
        LockSettingsValidator.Validate(
            scope,
            profile.LocalLockEnabled,
            profile.DistributedLockEnabled,
            profile.LocalLockTimeout,
            profile.DistributedLockTimeout,
            profile.DistributedLockExpiry);

    /// <summary>
    /// Validates the effective default lock (user <see cref="CacheOptions.DefaultCachePolicy"/>
    /// merged with provider builder lock) and any named policies merged on top. Runs at factory
    /// construction because the IValidateOptions pipeline can't see builder-derived defaults
    /// without forming a DI cycle through MultilayerCacheLockCrossOptionsValidator.
    /// </summary>
    internal static void ValidateEffectiveDefaultAndNamedLocks(CacheOptions options, LockProfile effectiveDefaultLock)
    {
        var single = ValidateLockProfile("EffectiveDefaultCachePolicy.Lock: ", effectiveDefaultLock);
        if (single.Failed)
        {
            throw new OptionsValidationException(nameof(CacheOptions), typeof(CacheOptions), single.Failures);
        }
        var cross = LockSettingsValidator.ValidateCross("EffectiveDefaultCachePolicy.Lock: ", options.DistributedLockPollInterval, effectiveDefaultLock.DistributedLockTimeout);
        if (cross.Failed)
        {
            throw new OptionsValidationException(nameof(CacheOptions), typeof(CacheOptions), cross.Failures);
        }
        ValidateNamedPoliciesAgainstDefaultLock(options, effectiveDefaultLock);
    }

    internal static void ValidateNamedPoliciesAgainstDefaultLock(CacheOptions options, LockProfile defaultLock)
    {
        var pollInterval = options.DistributedLockPollInterval;
        var policies = (IEnumerable<KeyValuePair<string, CachePolicy>>?)options.Policies ?? Array.Empty<KeyValuePair<string, CachePolicy>>();
        foreach (var (policyName, policy) in policies)
        {
            if (policy.Lock is null)
            {
                continue;
            }
            var merged = CachePolicyMerger.MergeLock(policy.Lock, defaultLock)!;
            var scope = $"Policies['{policyName}'].Lock: ";
            var single = ValidateLockProfile(scope, merged);
            if (single.Failed)
            {
                throw new OptionsValidationException(nameof(CacheOptions), typeof(CacheOptions), single.Failures);
            }
            var cross = LockSettingsValidator.ValidateCross(scope, pollInterval, merged.DistributedLockTimeout);
            if (cross.Failed)
            {
                throw new OptionsValidationException(nameof(CacheOptions), typeof(CacheOptions), cross.Failures);
            }
        }
    }
}
