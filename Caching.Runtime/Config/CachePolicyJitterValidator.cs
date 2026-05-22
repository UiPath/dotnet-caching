namespace UiPath.Platform.Caching.Config;

internal sealed class CachePolicyJitterValidator : IValidateOptions<CacheOptions>
{
    public ValidateOptionsResult Validate(string? name, CacheOptions options)
    {
        if (options.DefaultCachePolicy is { } defaultPolicy)
        {
            var result = ValidateJitter("DefaultCachePolicy.JitterMaxDuration", defaultPolicy.JitterMaxDuration);
            if (result.Failed)
            {
                return result;
            }
        }

        var policies = (IEnumerable<KeyValuePair<string, CachePolicy>>?)options.Policies ?? Array.Empty<KeyValuePair<string, CachePolicy>>();
        foreach (var (policyName, policy) in policies)
        {
            var result = ValidateJitter($"Policies['{policyName}'].JitterMaxDuration", policy.JitterMaxDuration);
            if (result.Failed)
            {
                return result;
            }
        }
        return ValidateOptionsResult.Success;
    }

    private static ValidateOptionsResult ValidateJitter(string scope, TimeSpan? jitter)
    {
        if (jitter is { } j && j < TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{scope} must be greater than or equal to zero. Actual: {j}.");
        }
        return ValidateOptionsResult.Success;
    }
}
