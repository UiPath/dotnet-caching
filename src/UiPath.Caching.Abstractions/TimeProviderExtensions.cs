namespace UiPath.Caching;

/// <summary>
/// Turning a lifetime into an expiration against the library's one clock, a <see cref="TimeProvider"/>.
/// <c>AddCaching</c> registers <see cref="TimeProvider.System"/> unless the container already has one, and
/// every cache, provider and helper takes it from there; nothing reads the ambient clock and nothing
/// carries a clock of its own, so a deadline is always computed and judged against the same time. The
/// arithmetic lives here so every provider — a test's fake included — converts the same way.
/// </summary>
public static class TimeProviderExtensions
{
    /// <summary>
    /// <paramref name="timeSpan"/> from now, or <see cref="DateTimeOffset.MaxValue"/> for
    /// <see langword="null"/>. No default is filled in: <see langword="null"/> means what the storage
    /// said — no TTL — and reads back as the sentinel the providers write for it.
    /// </summary>
    public static DateTimeOffset ToDateTimeOffset(this TimeProvider clock, TimeSpan? timeSpan) =>
        timeSpan.HasValue ? clock.ToExpiration(timeSpan.Value) : DateTimeOffset.MaxValue;

    /// <summary><paramref name="dateTimeOffset"/>, or <see cref="DateTimeOffset.MaxValue"/> for <see langword="null"/>.</summary>
    public static DateTimeOffset ToDateTimeOffset(this TimeProvider clock, DateTimeOffset? dateTimeOffset) =>
        dateTimeOffset ?? DateTimeOffset.MaxValue;

    /// <summary>
    /// <paramref name="duration"/> from now, saturating at <see cref="DateTimeOffset.MaxValue"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeSpan.MaxValue"/> is how a configured lifetime spells "no TTL", and it exceeds
    /// the remaining representable range from any modern date, so the conversion has to land on the
    /// sentinel the providers already read as "no TTL" rather than overflow on the way there.
    /// <see cref="TimeProvider.GetUtcNow"/> is UTC by contract; normalizing here makes that true of a
    /// fake as well, so one subtraction is the whole headroom check.
    /// </remarks>
    private static DateTimeOffset ToExpiration(this TimeProvider clock, TimeSpan duration)
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        return duration.Ticks > DateTime.MaxValue.Ticks - now.Ticks ? DateTimeOffset.MaxValue : now.Add(duration);
    }
}
