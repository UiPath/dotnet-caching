namespace UiPath.Caching.Config;

/// <summary>Bounds every inner-tier wait of the cache, and so the peer refresh: a zero or negative timeout would end each at once. Also rejects a zero or negative repeat-invalidation delay.</summary>
internal sealed class InMemoryRedisTimeoutValidator : IValidateOptions<InMemoryRedisCacheOptions>
{
    public ValidateOptionsResult Validate(string? name, InMemoryRedisCacheOptions options)
    {
        if (options.Timeout != Timeout.InfiniteTimeSpan && options.Timeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{nameof(InMemoryRedisCacheOptions.Timeout)} must be greater than zero, or Timeout.InfiniteTimeSpan for no bound.");
        }

        return options.RepeatInvalidationAfter is { } repeat && repeat <= TimeSpan.Zero
            ? ValidateOptionsResult.Fail($"{nameof(InMemoryRedisCacheOptions.RepeatInvalidationAfter)} must be greater than zero, or null for no repeat.")
            : ValidateOptionsResult.Success;
    }
}
