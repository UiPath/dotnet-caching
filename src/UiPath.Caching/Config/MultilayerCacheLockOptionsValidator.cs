namespace UiPath.Caching.Config;

internal sealed class MultilayerCacheLockOptionsValidator<TOptions> : IValidateOptions<TOptions>
    where TOptions : class, IMultilayerCacheOptions
{
    public ValidateOptionsResult Validate(string? name, TOptions options) =>
        LockSettingsValidator.Validate(
            scope: string.Empty,
            options.LocalLockEnabled,
            options.DistributedLockEnabled,
            options.LocalLockTimeout,
            options.DistributedLockTimeout,
            options.DistributedLockExpiry);
}
