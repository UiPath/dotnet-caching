using System.Runtime.CompilerServices;

namespace UiPath.Caching;

/// <summary>
/// Argument validation for the per-call <c>expiration</c> on the write surface.
/// </summary>
/// <remarks>
/// The write members take a non-nullable <see cref="TimeSpan"/> / <see cref="DateTimeOffset"/>, so
/// there is no <c>null</c> left to absorb a nonsensical value: a caller with nothing to say about
/// lifetime calls the overload that has no <c>expiration</c> parameter and gets
/// <see cref="CachePolicy.DistributedExpiration"/>, then the cache default. What remains is a value
/// the caller meant, and a duration that is not positive — or a deadline that has already passed —
/// cannot be honored, so it is rejected rather than silently swallowed.
/// <para>
/// This does not police the resolved default: a policy or provider default that leaves entries
/// unbounded still yields <see cref="TimeSpan.MaxValue"/> / <see cref="DateTimeOffset.MaxValue"/>,
/// which the providers read as "no TTL". Those two sentinels stay valid inputs here.
/// </para>
/// <para>
/// Enforcement sits in the implementations that honor the lifetime. <see cref="NullCache"/> and its
/// siblings read no argument at all — not the key, not the type, not the expiration — so they keep
/// degrading to "caching is off, carry on" rather than throwing on a value they never look at.
/// </para>
/// </remarks>
public static class CacheExpiration
{
    /// <summary>Returns <paramref name="expiration"/>, or throws if it is not a positive duration.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expiration"/> is zero or negative.</exception>
    public static TimeSpan ThrowIfNotPositive(TimeSpan expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null)
    {
        if (expiration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                expiration,
                "The cache expiration must be a positive duration. To inherit the policy's DistributedExpiration or the cache default, call the overload without an expiration argument.");
        }

        return expiration;
    }

    /// <summary>Returns <paramref name="expiration"/>, or throws if it is not later than <paramref name="now"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expiration"/> is at or before <paramref name="now"/>.</exception>
    public static DateTimeOffset ThrowIfNotFuture(DateTimeOffset expiration, DateTimeOffset now, [CallerArgumentExpression(nameof(expiration))] string? paramName = null)
    {
        if (expiration <= now)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                expiration,
                "The cache expiration must be later than the cache's current time. To inherit the policy's DistributedExpiration or the cache default, call the overload without an expiration argument.");
        }

        return expiration;
    }

    /// <summary>Validates a caller deadline against <paramref name="now"/> and returns it as a duration from <paramref name="now"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expiration"/> is at or before <paramref name="now"/>.</exception>
    public static TimeSpan ToDuration(DateTimeOffset expiration, DateTimeOffset now, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        ThrowIfNotFuture(expiration, now, paramName) - now;
}
