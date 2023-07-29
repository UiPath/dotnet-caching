namespace UiPath.Platform.Caching;

public interface IMemoryStatisticsOptions
{
    bool TrackStatistics { get; }

    TimeSpan StatisticsFlushInterval { get; }
}
