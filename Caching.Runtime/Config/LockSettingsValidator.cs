namespace UiPath.Platform.Caching.Config;

internal static class LockSettingsValidator
{
    internal static readonly TimeSpan DefaultDistributedLockTimeout = TimeSpan.FromMilliseconds(500);

    public static ValidateOptionsResult Validate(
        string scope,
        bool? localLockEnabled,
        bool? distributedLockEnabled,
        TimeSpan? localLockTimeout,
        TimeSpan? distributedLockTimeout,
        TimeSpan? distributedLockExpiry)
    {
        if (distributedLockExpiry is { } expiry && expiry <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{scope}{nameof(LockProfile.DistributedLockExpiry)} must be greater than zero.");
        }
        if (distributedLockTimeout is { } timeout && timeout < TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{scope}{nameof(LockProfile.DistributedLockTimeout)} must be greater than or equal to zero.");
        }
        if (localLockTimeout is { } localTimeout && localTimeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{scope}{nameof(LockProfile.LocalLockTimeout)} must be greater than zero.");
        }
        if (localLockEnabled == false && distributedLockEnabled == true)
        {
            return ValidateOptionsResult.Fail(
                $"{scope}{nameof(LockProfile.LocalLockEnabled)}=false with " +
                $"{nameof(LockProfile.DistributedLockEnabled)}=true weakens single-flight: " +
                "every local caller competes independently for the distributed lock, multiplying " +
                "LockTakeAsync round-trips per node. Enable both or disable both.");
        }
        return ValidateOptionsResult.Success;
    }

    public static ValidateOptionsResult ValidateCross(
        string scope,
        TimeSpan distributedLockPollInterval,
        TimeSpan? distributedLockTimeout)
    {
        var effective = distributedLockTimeout ?? DefaultDistributedLockTimeout;
        if (distributedLockPollInterval > effective)
        {
            return ValidateOptionsResult.Fail(
                $"{scope}{nameof(CacheOptions.DistributedLockPollInterval)} ({distributedLockPollInterval}) " +
                $"must be <= {nameof(LockProfile.DistributedLockTimeout)} ({effective}); " +
                "otherwise the first retry sleep clamps to the remaining wait budget, neutralizing exponential backoff and limiting the loop to ~1–2 acquire attempts.");
        }
        return ValidateOptionsResult.Success;
    }
}
