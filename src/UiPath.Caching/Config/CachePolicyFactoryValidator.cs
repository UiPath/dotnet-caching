namespace UiPath.Caching.Config;

/// <summary>
/// Validates the policies exposed by an <see cref="ICachePolicyFactory"/>. Iterates
/// <see cref="ICachePolicyFactory.Keys"/> + <see cref="ICachePolicyFactory.Resolve"/> for the
/// per-name pass and <see cref="ICachePolicyFactory.Default"/> for the default-policy pass.
/// Throws <see cref="OptionsValidationException"/> on the first invalid combination.
/// </summary>
/// <remarks>
/// Custom <c>ICachePolicyFactory</c> implementations can call <see cref="Validate"/> at the end
/// of their constructor to opt into the same per-policy validation rules that
/// <c>DefaultCachePolicyFactory</c> applies. Each cache implementation calls
/// <see cref="ValidateAgainstEffectiveDefault"/> from its own constructor to cross-validate
/// the factory's named policies against the cache's effective default lock.
/// </remarks>
public static class CachePolicyFactoryValidator
{
    /// <summary>
    /// Per-policy validation. Validates the factory's default policy (when set) and every named
    /// policy returned by <see cref="ICachePolicyFactory.Resolve"/>.
    /// </summary>
    /// <param name="factory">The factory to validate. Must be functionally complete (Keys / Resolve / Default already return the final values).</param>
    /// <param name="distributedLockPollInterval">Used to cross-check <c>DistributedLockTimeout</c> against the cache-wide poll interval.</param>
    public static void Validate(
        ICachePolicyFactory factory,
        TimeSpan distributedLockPollInterval)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (factory.Default is { } defaultPolicy)
        {
            ValidatePolicy("DefaultCachePolicy", defaultPolicy, distributedLockPollInterval);
        }
        foreach (var name in factory.Keys)
        {
            if (factory.Resolve(name) is { } merged)
            {
                ValidatePolicy($"Policies['{name}']", merged, distributedLockPollInterval);
            }
        }
    }

    /// <summary>
    /// Validates the full effective default policy of a cache instance (provider-specific defaults
    /// merged with the user-configured default), then cross-validates each named policy's lock
    /// merged against that effective default lock. Called from each cache implementation's ctor.
    /// </summary>
    public static void ValidateAgainstEffectiveDefault(
        ICachePolicyFactory factory,
        TimeSpan distributedLockPollInterval,
        CachePolicy effectiveDefault)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(effectiveDefault);

        ValidatePolicy("EffectiveDefaultCachePolicy", effectiveDefault, distributedLockPollInterval);

        if (effectiveDefault.Lock is not { } effectiveDefaultLock)
        {
            return;
        }
        foreach (var name in factory.Keys)
        {
            if (factory.Resolve(name) is not { Lock: { } namedLock })
            {
                continue;
            }
            var mergedLock = CachePolicyMerger.MergeLock(namedLock, effectiveDefaultLock)!;
            var lockScope = $"Policies['{name}'].Lock: ";
            Throw(LockSettingsValidator.Validate(lockScope, mergedLock.LocalLockEnabled, mergedLock.DistributedLockEnabled, mergedLock.LocalLockTimeout, mergedLock.DistributedLockTimeout, mergedLock.DistributedLockExpiry));
            Throw(LockSettingsValidator.ValidateCross(lockScope, distributedLockPollInterval, mergedLock.DistributedLockTimeout));
        }
    }

    private static void ValidatePolicy(string scope, CachePolicy policy, TimeSpan pollInterval)
    {
        if (policy.LocalExpiration is { } local
            && policy.LocalExpirationDisconnected is { } localDisconnected
            && localDisconnected > local)
        {
            Throw(ValidateOptionsResult.Fail($"{scope}.LocalExpirationDisconnected ({localDisconnected}) must be less than or equal to {scope}.LocalExpiration ({local})."));
        }
        if (policy.Lock is { } lockProfile)
        {
            var lockScope = $"{scope}.Lock: ";
            Throw(LockSettingsValidator.Validate(lockScope, lockProfile.LocalLockEnabled, lockProfile.DistributedLockEnabled, lockProfile.LocalLockTimeout, lockProfile.DistributedLockTimeout, lockProfile.DistributedLockExpiry));
            Throw(LockSettingsValidator.ValidateCross(lockScope, pollInterval, lockProfile.DistributedLockTimeout));
        }
        if (policy.JitterMaxDuration is { } jitter && jitter < TimeSpan.Zero)
        {
            Throw(ValidateOptionsResult.Fail($"{scope}.JitterMaxDuration must be greater than or equal to zero. Actual: {jitter}."));
        }
        if (policy.Rehydrate is { } rehydrate)
        {
            ValidateRehydrate($"{scope}.Rehydrate: ", rehydrate);
        }
    }

    private static void ValidateRehydrate(string scope, RehydrateOptions opts)
    {
        if (opts.Threshold <= 0 || opts.Threshold > 1)
        {
            Throw(ValidateOptionsResult.Fail($"{scope}Threshold must be in (0, 1]. Actual: {opts.Threshold}."));
        }
        if (opts.TimeoutFraction <= 0 || opts.TimeoutFraction > 1)
        {
            Throw(ValidateOptionsResult.Fail($"{scope}TimeoutFraction must be in (0, 1]. Actual: {opts.TimeoutFraction}."));
        }
        if (opts.BaseCooldown <= TimeSpan.Zero)
        {
            Throw(ValidateOptionsResult.Fail($"{scope}BaseCooldown must be greater than zero. Actual: {opts.BaseCooldown}."));
        }
        if (opts.MaxCooldown <= TimeSpan.Zero)
        {
            Throw(ValidateOptionsResult.Fail($"{scope}MaxCooldown must be greater than zero. Actual: {opts.MaxCooldown}."));
        }
        if (opts.MaxCooldown < opts.BaseCooldown)
        {
            Throw(ValidateOptionsResult.Fail($"{scope}MaxCooldown ({opts.MaxCooldown}) must be greater than or equal to BaseCooldown ({opts.BaseCooldown})."));
        }
    }

    private static void Throw(ValidateOptionsResult result)
    {
        if (result.Failed)
        {
            throw new OptionsValidationException(nameof(CacheOptions), typeof(CacheOptions), result.Failures);
        }
    }
}
