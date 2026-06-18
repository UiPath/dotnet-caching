namespace UiPath.Caching;

public sealed class CacheClock
{
    private readonly ISystemClock _clock;
    private readonly TimeSpan? _defaultExpiration;

    public CacheClock(ISystemClock? clock = null, TimeSpan? defaultExpiration = null)
    {
        _clock = clock ?? new SystemClock();
        _defaultExpiration = defaultExpiration;
    }

    public DateTimeOffset UtcNow => _clock.UtcNow;

    public DateTimeOffset DefaultDateTimeOffset() =>
        ToDateTimeOffset(default(DateTimeOffset?));

    public DateTimeOffset ToDateTimeOffset(TimeSpan? timeSpan)
    {
        if (_defaultExpiration.HasValue)
        {
            return _clock.UtcNow.Add(timeSpan ?? _defaultExpiration.Value);
        }

        return timeSpan.HasValue ? _clock.UtcNow.Add(timeSpan.Value) : DateTimeOffset.MaxValue;
    }

    public DateTimeOffset ToDateTimeOffset(DateTimeOffset? dateTimeOffset) =>
        dateTimeOffset ?? (_defaultExpiration.HasValue ? _clock.UtcNow.Add(_defaultExpiration.Value) : DateTimeOffset.MaxValue);

    public TimeSpan ToTimeSpan(DateTimeOffset? dateTimeOffset)
    {
        if (_defaultExpiration.HasValue)
        {
            return (dateTimeOffset ?? _clock.UtcNow.Add(_defaultExpiration.Value)).Subtract(_clock.UtcNow);
        }
        return dateTimeOffset == null ? TimeSpan.MaxValue : dateTimeOffset.Value.Subtract(_clock.UtcNow);
    }

    public TimeSpan ToTimeSpan(TimeSpan? timeSpan) =>
        timeSpan ?? _defaultExpiration ?? TimeSpan.MaxValue;
}
