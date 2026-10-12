namespace UiPath.Caching.Config;

internal sealed class CacheOptionsLockValidator : IValidateOptions<CacheOptions>
{
    public ValidateOptionsResult Validate(string? name, CacheOptions options)
    {
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
