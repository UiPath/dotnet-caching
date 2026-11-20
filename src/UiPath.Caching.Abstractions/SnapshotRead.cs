namespace UiPath.Caching;

/// <param name="Outcome">What the read found.</param>
/// <param name="Entry">The value and its expiration, set only for <see cref="SnapshotOutcome.Found"/>.</param>
public readonly record struct SnapshotRead<T>(SnapshotOutcome Outcome, ICacheEntry<T?>? Entry);
