namespace UiPath.Platform.Caching.Config;

internal sealed class MultilayerCacheLockCrossOptionsValidator<TOptions> : IValidateOptions<TOptions>
    where TOptions : class, IMultilayerCacheOptions
{
    private readonly CacheOptions _cacheOptions;

    public MultilayerCacheLockCrossOptionsValidator(IOptions<CacheOptions> cacheOptions) =>
        _cacheOptions = cacheOptions.Value;

    public ValidateOptionsResult Validate(string? name, TOptions options) =>
        LockSettingsValidator.ValidateCross(
            scope: string.Empty,
            _cacheOptions.DistributedLockPollInterval,
            options.DistributedLockTimeout);
}
