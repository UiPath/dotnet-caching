namespace UiPath.Caching;

/// <summary>
/// The default <see cref="ICacheClock"/>: the <see cref="ISystemClock"/> the container registered, else
/// the system clock. <c>AddCaching</c> registers one instance, and it is the only clock in the library.
/// </summary>
public sealed class CacheClock(ISystemClock? clock = null) : ICacheClock
{
    private readonly ISystemClock _clock = clock ?? new SystemClock();

    public DateTimeOffset UtcNow => _clock.UtcNow;
}
