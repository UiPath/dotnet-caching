namespace UiPath.Caching;

/// <param name="Outcome">What the read found.</param>
/// <param name="Entry">The fields with the metadata and the expiration, set only for <see cref="SnapshotOutcome.Found"/>.</param>
internal readonly record struct HashSnapshotRead<T>(SnapshotOutcome Outcome, ICacheEntry<IDictionary<string, T?>>? Entry);
