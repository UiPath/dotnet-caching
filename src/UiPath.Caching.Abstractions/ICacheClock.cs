namespace UiPath.Caching;

/// <summary>
/// The one source of "now" for every expiration decision in the library. <c>AddCaching</c> registers
/// a single instance — over the <c>ISystemClock</c> in the container when one is registered, else the
/// system clock — and every cache, provider and helper takes it from there. Nothing reads the ambient
/// clock, and nothing carries a clock of its own, so a deadline is always computed and judged against
/// the same time.
/// </summary>
public interface ICacheClock
{
    DateTimeOffset UtcNow { get; }
}
