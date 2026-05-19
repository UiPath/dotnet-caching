namespace UiPath.Platform.Caching.Config;

internal sealed class CachePolicyRehydrateValidator : IValidateOptions<CacheOptions>
{
    public ValidateOptionsResult Validate(string? name, CacheOptions options)
    {
        if (options.DefaultCachePolicy?.Rehydrate is { } defaultRehydrate)
        {
            var result = ValidateRehydrateOptions("DefaultCachePolicy.Rehydrate: ", defaultRehydrate);
            if (result.Failed)
            {
                return result;
            }
        }

        // CacheOptions.Policies has a public setter — configuration binding can leave it null.
        // Treat null as "no named policies" rather than NRE'ing at startup.
        var policies = (IEnumerable<KeyValuePair<string, CachePolicy>>?)options.Policies ?? Array.Empty<KeyValuePair<string, CachePolicy>>();
        foreach (var (policyName, policy) in policies)
        {
            if (policy.Rehydrate is not { } namedRehydrate)
            {
                continue;
            }
            var result = ValidateRehydrateOptions($"Policies['{policyName}'].Rehydrate: ", namedRehydrate);
            if (result.Failed)
            {
                return result;
            }
        }
        return ValidateOptionsResult.Success;
    }

    private static ValidateOptionsResult ValidateRehydrateOptions(string scope, RehydrateOptions opts)
    {
        if (opts.Threshold <= 0 || opts.Threshold > 1)
        {
            return ValidateOptionsResult.Fail($"{scope}Threshold must be in (0, 1]. Actual: {opts.Threshold}.");
        }
        if (opts.TimeoutFraction <= 0 || opts.TimeoutFraction > 1)
        {
            return ValidateOptionsResult.Fail($"{scope}TimeoutFraction must be in (0, 1]. Actual: {opts.TimeoutFraction}.");
        }
        if (opts.BaseCooldown <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{scope}BaseCooldown must be greater than zero. Actual: {opts.BaseCooldown}.");
        }
        if (opts.MaxCooldown <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{scope}MaxCooldown must be greater than zero. Actual: {opts.MaxCooldown}.");
        }
        if (opts.MaxCooldown < opts.BaseCooldown)
        {
            return ValidateOptionsResult.Fail($"{scope}MaxCooldown ({opts.MaxCooldown}) must be greater than or equal to BaseCooldown ({opts.BaseCooldown}).");
        }
        return ValidateOptionsResult.Success;
    }
}
