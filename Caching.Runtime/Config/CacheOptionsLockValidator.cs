namespace UiPath.Platform.Caching.Config;

internal sealed class CacheOptionsLockValidator : IValidateOptions<CacheOptions>
{
    public ValidateOptionsResult Validate(string? name, CacheOptions options)
    {
        if (options.LocalLockPoolSize <= 0)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(CacheOptions.LocalLockPoolSize)} must be greater than zero.");
        }
        if (options.LocalLockPoolInitialFill < 0 || options.LocalLockPoolInitialFill > options.LocalLockPoolSize)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(CacheOptions.LocalLockPoolInitialFill)} must be between 0 and {nameof(CacheOptions.LocalLockPoolSize)}.");
        }
        if (options.DistributedLockPollInterval <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(CacheOptions.DistributedLockPollInterval)} must be greater than zero.");
        }
        if (options.DistributedLockMaxPollInterval < options.DistributedLockPollInterval)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(CacheOptions.DistributedLockMaxPollInterval)} must be greater than or equal to {nameof(CacheOptions.DistributedLockPollInterval)}.");
        }
        return ValidateOptionsResult.Success;
    }
}
