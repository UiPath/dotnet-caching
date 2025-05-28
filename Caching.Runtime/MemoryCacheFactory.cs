namespace UiPath.Platform.Caching;

public sealed class MemoryCacheFactory(ISystemClock? clock, ILoggerFactory loggerFactory) : IMemoryCacheFactory
{
    public IMemoryCache Get(IMemoryCacheOptions memoryOptions)
    {
        var memoryCacheOptions = new MemoryCacheOptions
        {
#if !NET6_0
               TrackStatistics = memoryOptions.TrackStatistics,
#endif
            Clock = clock
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
}
