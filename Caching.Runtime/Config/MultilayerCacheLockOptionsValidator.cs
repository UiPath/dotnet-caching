namespace UiPath.Platform.Caching.Config;

internal sealed class MultilayerCacheLockOptionsValidator<TOptions> : IValidateOptions<TOptions>
    where TOptions : class, IMultilayerCacheOptions
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        if (options.DistributedLockExpiry is { } expiry && expiry <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(IMultilayerCacheOptions.DistributedLockExpiry)} must be greater than zero.");
        }
        if (options.DistributedLockTimeout is { } timeout && timeout < TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(IMultilayerCacheOptions.DistributedLockTimeout)} must be greater than or equal to zero.");
        }
        if (options.LocalLockTimeout is { } localTimeout && localTimeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(IMultilayerCacheOptions.LocalLockTimeout)} must be greater than zero.");
        }
        if (options.LocalLockEnabled == false && options.DistributedLockEnabled == true)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(IMultilayerCacheOptions.LocalLockEnabled)}=false with " +
                $"{nameof(IMultilayerCacheOptions.DistributedLockEnabled)}=true weakens single-flight: " +
                "every local caller competes independently for the distributed lock, multiplying " +
                "LockTakeAsync round-trips per node. Enable both or disable both.");
        }
        return ValidateOptionsResult.Success;
    }
}
