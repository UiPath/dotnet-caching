namespace UiPath.Caching;

public sealed class MemoryCacheFactory(TimeProvider clock, ILoggerFactory loggerFactory) : IMemoryCacheFactory
{
    // MemoryCache reads an ISystemClock; adapting the one TimeProvider keeps it judging deadlines by the same "now".
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

    private sealed class SystemClockAdapter(TimeProvider clock) : ISystemClock
    {
        public DateTimeOffset UtcNow => clock.GetUtcNow();
    }
}
