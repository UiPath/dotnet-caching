namespace UiPath.Platform.Caching;

public interface IMemoryCacheOptions
{
    bool TrackStatistics { get; }

    TimeSpan StatisticsFlushInterval { get; }

    long? SizeLimit { get; }

    double? CompactionPercentage { get; }

    ICacheEntrySizeProvider? SizeProvider { get; }
}
