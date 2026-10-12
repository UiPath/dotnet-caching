namespace UiPath.Caching;

/// <summary>
/// Per-cache-instance overrides for the lock fields on <c>InMemoryCacheOptions</c>. Field names
/// mirror the shipped cache-wide options exactly so resolution is unambiguous. A field set to
/// <c>null</c> inherits from the default policy (and ultimately from <c>InMemoryCacheOptions</c>).
/// </summary>
public sealed class LockProfile
{
    [Obsolete("Use LocalSingleFlightEnabled; the in-process lock it named is gone.")]
    public bool? LocalLockEnabled { get => LocalSingleFlightEnabled; init => LocalSingleFlightEnabled = value; }

    [Obsolete("Use LocalSingleFlightTimeout; the in-process lock it named is gone.")]
    public TimeSpan? LocalLockTimeout { get => LocalSingleFlightTimeout; init => LocalSingleFlightTimeout = value; }

    public bool? LocalSingleFlightEnabled { get; init; }

    public bool? DistributedLockEnabled { get; init; }

    public TimeSpan? LocalSingleFlightTimeout { get; init; }

    public TimeSpan? DistributedLockTimeout { get; init; }

    public TimeSpan? DistributedLockExpiry { get; init; }
}
