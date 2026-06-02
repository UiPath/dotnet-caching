namespace UiPath.Platform.Caching;

/// <summary>
/// Resolves the <see cref="CachePolicy"/> for a named cache instance. <see cref="ICache{T}"/> and
/// <see cref="IHashCache{T}"/> bind a policy by name at construction (defaulting to
/// <c>typeof(T).FullName!</c>). The default implementation does an O(1) dictionary lookup over
/// pre-merged named policies; consumers can replace it via DI for custom resolution.
/// </summary>
/// <remarks>
/// Implementations MUST be fast (single-digit microseconds) and MUST NOT throw. Both members
/// return <c>null</c> when there is no configured policy — cache implementations fall back to
/// their provider's own effective default at construction time.
/// </remarks>
public interface ICachePolicyFactory
{
    /// <summary>
    /// Resolves the named cache policy. Returns <c>null</c> when no specific policy is registered
    /// for <paramref name="policyName"/>.
    /// </summary>
    CachePolicy? Resolve(string policyName);

    /// <summary>
    /// The user-configured default policy. Returns <c>null</c> when no default is configured —
    /// each cache implementation then materializes its own effective default from its provider
    /// options at construction.
    /// </summary>
    CachePolicy? Default { get; }

    /// <summary>
    /// Names of all registered policies. Validators iterate this and call <see cref="Resolve"/>
    /// for each to validate the merged effective policy. Custom factories that don't enumerate
    /// statically may return an empty sequence and opt out of validation.
    /// </summary>
    IEnumerable<string> Keys { get; }
}
