namespace UiPath.Caching;

/// <param name="Outcome">What the call did.</param>
/// <param name="Existing">The value the key already held, set only for <see cref="FillOutcome.Existed"/>.</param>
/// <param name="Expiration">When the stored entry leaves storage (<see cref="DateTimeOffset.MaxValue"/> for no TTL), or null when unknown.</param>
public readonly record struct FillResult<T>(FillOutcome Outcome, T? Existing, DateTimeOffset? Expiration = null);
