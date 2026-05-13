namespace UiPath.Platform.Caching.Config;

internal sealed class MultilayerCacheLockCrossOptionsValidator<TOptions> : IValidateOptions<TOptions>
    where TOptions : class, IMultilayerCacheOptions
{
    private static readonly TimeSpan DefaultDistributedLockTimeout = TimeSpan.FromMilliseconds(500);

    private readonly CacheOptions _cacheOptions;

    public MultilayerCacheLockCrossOptionsValidator(IOptions<CacheOptions> cacheOptions) =>
        _cacheOptions = cacheOptions.Value;

    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        var effectiveTimeout = options.DistributedLockTimeout ?? DefaultDistributedLockTimeout;
        if (_cacheOptions.DistributedLockPollInterval > effectiveTimeout)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(CacheOptions.DistributedLockPollInterval)} ({_cacheOptions.DistributedLockPollInterval}) " +
                $"must be <= {nameof(IMultilayerCacheOptions.DistributedLockTimeout)} ({effectiveTimeout}); " +
                "otherwise the first retry sleep clamps to the remaining wait budget, neutralizing exponential backoff and limiting the loop to ~1–2 acquire attempts.");
        }
        return ValidateOptionsResult.Success;
    }
}
