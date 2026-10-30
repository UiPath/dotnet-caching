namespace UiPath.Caching.Config;

/// <summary>Bounds every inner-tier wait of the cache, and so the peer refresh: a zero or negative timeout would end each at once.</summary>
internal sealed class InMemoryRedisTimeoutValidator : IValidateOptions<InMemoryRedisCacheOptions>
{
    public ValidateOptionsResult Validate(string? name, InMemoryRedisCacheOptions options) =>
        options.Timeout == Timeout.InfiniteTimeSpan || options.Timeout > TimeSpan.Zero
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{nameof(InMemoryRedisCacheOptions.Timeout)} must be greater than zero, or Timeout.InfiniteTimeSpan for no bound.");
}
