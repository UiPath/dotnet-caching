namespace UiPath.Caching.Redis;

/// <summary>
/// Runs the separator-dependent keyspace checks when <see cref="CacheOptions"/> is first resolved, so they
/// hold for package-to-package layouts and not only where the distributed cache probes them.
/// </summary>
internal sealed class ReservedRedisKeyspaceValidator(IEnumerable<IReservedRedisKeyspace> reserved)
    : IValidateOptions<CacheOptions>
{
    public ValidateOptionsResult Validate(string? name, CacheOptions options)
    {
        try
        {
            reserved.ValidatedFor(options.Separator);
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}
