namespace UiPath.Caching;

public sealed class MemoryCacheFactory(ICacheClock clock, ILoggerFactory loggerFactory) : IMemoryCacheFactory
{
    // MemoryCache reads an ISystemClock; this is the library's one clock in that shape, so the
    // memory cache judges a deadline against the same "now" it was computed from.
    private readonly ISystemClock _systemClock = new SystemClockAdapter(clock);

    public IMemoryCache Get(IMemoryCacheOptions memoryOptions)
    {
        var memoryCacheOptions = new MemoryCacheOptions
        {
            TrackStatistics = memoryOptions.TrackStatistics,
            Clock = _systemClock
        };

        if (memoryOptions.SizeLimit > 0)
        {
            memoryCacheOptions.SizeLimit = memoryOptions.SizeLimit;
        }

        if (memoryOptions.CompactionPercentage > 0 && memoryCacheOptions.CompactionPercentage < 1)
        {
            memoryCacheOptions.CompactionPercentage = memoryOptions.CompactionPercentage.Value;
        }

        return new MemoryCache(Options.Create(memoryCacheOptions), loggerFactory);
    }

    private sealed class SystemClockAdapter(ICacheClock clock) : ISystemClock
    {
        public DateTimeOffset UtcNow => clock.UtcNow;
    }
}
