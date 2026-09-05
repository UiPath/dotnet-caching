namespace UiPath.Caching;

/// <summary>Lifetime-to-expiration conversions against the library's one clock, the DI <see cref="TimeProvider"/>.</summary>
public static class TimeProviderExtensions
{
    /// <summary><paramref name="timeSpan"/> from now; <see langword="null"/> is "no TTL" and reads as <see cref="DateTimeOffset.MaxValue"/>.</summary>
    public static DateTimeOffset ToDateTimeOffset(this TimeProvider clock, TimeSpan? timeSpan) =>
        timeSpan.HasValue ? clock.ToExpiration(timeSpan.Value) : DateTimeOffset.MaxValue;

    /// <summary><paramref name="dateTimeOffset"/>, or <see cref="DateTimeOffset.MaxValue"/> for <see langword="null"/>.</summary>
    public static DateTimeOffset ToDateTimeOffset(this TimeProvider clock, DateTimeOffset? dateTimeOffset) =>
        dateTimeOffset ?? DateTimeOffset.MaxValue;

    /// <summary><paramref name="duration"/> from now, saturating at <see cref="DateTimeOffset.MaxValue"/>.</summary>
    /// <remarks>
    /// <see cref="TimeSpan.MaxValue"/> spells "no TTL" and exceeds the representable range, so it lands on the sentinel
    /// instead of overflowing. Normalizing to UTC first makes one subtraction the whole headroom check.
    /// </remarks>
    private static DateTimeOffset ToExpiration(this TimeProvider clock, TimeSpan duration)
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        return duration.Ticks > DateTime.MaxValue.Ticks - now.Ticks ? DateTimeOffset.MaxValue : now.Add(duration);
    }
}
