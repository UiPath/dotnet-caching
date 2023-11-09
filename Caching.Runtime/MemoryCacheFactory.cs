namespace UiPath.Platform.Caching;

public sealed class MemoryCacheFactory : IMemoryCacheFactory
{
    private readonly ISystemClock? _clock;
    private readonly ILoggerFactory _loggerFactory;

    public MemoryCacheFactory(ISystemClock? clock, ILoggerFactory? loggerFactory)
    {
        _clock = clock;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public IMemoryCache Get(IMemoryStatisticsOptions memoryStatisticsOptions) =>
        new MemoryCache(
           Options.Create(new MemoryCacheOptions
           {
               Clock = _clock,
               TrackStatistics = memoryStatisticsOptions.TrackStatistics
           }),
           _loggerFactory);
}
