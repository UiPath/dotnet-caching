namespace UiPath.Platform.Caching;

/// <summary>
/// Resolves the <see cref="CachePolicy"/> for a named cache instance. <see cref="ICache{T}"/> and
/// <see cref="IHashCache{T}"/> bind a policy by name at construction (defaulting to
/// <c>typeof(T).FullName!</c>). The default implementation does an O(1) dictionary lookup over
/// pre-merged named policies; consumers can replace it via DI for custom resolution.
/// </summary>
/// <remarks>
/// Implementations MUST be fast (single-digit microseconds), MUST NOT throw, and MUST return
/// non-null. <see cref="CachePolicy.Empty"/> is the conventional fallback when the name is not
/// registered and no default policy is configured.
/// </remarks>
public interface ICachePolicyFactory
{
    CachePolicy Resolve(string policyName);

    /// <summary>
    /// The pre-merged default policy. Returned when a caller has no name to bind (e.g., direct
    /// <see cref="ICache"/> / <see cref="IHashCache"/> usage with <c>policy: null</c>) so cache-wide
    /// tuning from <c>CacheOptions.DefaultCachePolicy</c> and the provider's
    /// <c>IMultilayerCacheOptions</c> backcompat shim still applies. Always non-null;
    /// implementations with no configured default return <see cref="CachePolicy.Empty"/>.
    /// </summary>
    CachePolicy Default { get; }
}
