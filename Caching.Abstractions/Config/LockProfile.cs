namespace UiPath.Platform.Caching;

/// <summary>
/// Per-cache-instance overrides for the lock fields on <c>InMemoryCacheOptions</c>. Field names
/// mirror the shipped cache-wide options exactly so resolution is unambiguous. A field set to
/// <c>null</c> inherits from the default policy (and ultimately from <c>InMemoryCacheOptions</c>).
/// </summary>
public sealed class LockProfile
{
    public bool? LocalLockEnabled { get; init; }

    public bool? DistributedLockEnabled { get; init; }

    public TimeSpan? LocalLockTimeout { get; init; }

    public TimeSpan? DistributedLockTimeout { get; init; }

    public TimeSpan? DistributedLockExpiry { get; init; }
}
