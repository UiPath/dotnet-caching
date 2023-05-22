namespace UiPath.Platform.Caching;

public abstract class CacheBase
{
    private readonly CacheOptions _cacheOptions;

    protected CacheBase(CacheOptions cacheOptions)
    {
        _cacheOptions = cacheOptions;
        Clock = cacheOptions.Clock ?? new SystemClock();
        CacheEntryFactory = cacheOptions.EntryFactory ?? new CacheEntryFactory();
    }

    protected ISystemClock Clock { get; private set; }

    protected ICacheEntryFactory CacheEntryFactory { get; private set; }

    protected DateTimeOffset DefaultDateTimeOffset() =>
        ToDateTimeOffset(default(DateTimeOffset?));

    protected DateTimeOffset ToDateTimeOffset(TimeSpan? timeSpan)
    {
        if (_cacheOptions.DefaultExpiration.HasValue)
        {
            return Clock.UtcNow.Add(timeSpan ?? _cacheOptions.DefaultExpiration.Value);
        }

        return timeSpan.HasValue ? Clock.UtcNow.Add(timeSpan.Value) : DateTimeOffset.MaxValue;
    }

    protected DateTimeOffset ToDateTimeOffset(DateTimeOffset? dateTimeOffset) =>
        dateTimeOffset ?? (_cacheOptions.DefaultExpiration.HasValue ? Clock.UtcNow.Add(_cacheOptions.DefaultExpiration.Value) : DateTimeOffset.MaxValue);

    protected TimeSpan ToTimeSpan(DateTimeOffset? dateTimeOffset)
    {
        if (_cacheOptions.DefaultExpiration.HasValue)
        {
            return (dateTimeOffset ?? Clock.UtcNow.Add(_cacheOptions.DefaultExpiration.Value)).Subtract(Clock.UtcNow);
        }
        return dateTimeOffset == null ? TimeSpan.MaxValue : dateTimeOffset.Value.Subtract(Clock.UtcNow);
    }

    protected TimeSpan ToTimeSpan(TimeSpan? timeSpan) =>
        timeSpan ?? _cacheOptions.DefaultExpiration ?? TimeSpan.MaxValue;
}
