namespace UiPath.Caching;

/// <param name="Outcome">What the call did.</param>
/// <param name="Current">The value the key holds after the call.</param>
/// <param name="Expiration">When the stored entry leaves storage (<see cref="DateTimeOffset.MaxValue"/> for no TTL), or null when unknown.</param>
public readonly record struct ChangeResult<T>(ChangeOutcome Outcome, T? Current, DateTimeOffset? Expiration = null);
