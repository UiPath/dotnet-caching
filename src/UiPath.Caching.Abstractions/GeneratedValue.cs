namespace UiPath.Caching;

/// <summary>What a generator returns when the value knows its own lifetime; a null <paramref name="Expiration"/> leaves it to the policy.</summary>
public readonly record struct GeneratedValue<T>(T? Value, DateTimeOffset? Expiration = null)
{
    /// <summary>The value is cached but not kept for <see cref="CachePolicy.FailSafeEnabled"/>, and the value kept for the key before is dropped: for a negative result or a placeholder that must not outlive its own expiration.</summary>
    public bool SkipFailSafe { get; init; }
}
