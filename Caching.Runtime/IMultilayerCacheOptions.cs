namespace UiPath.Platform.Caching;

public interface IMultilayerCacheOptions : ICacheOptions
{
    public string? Topic { get; set; }

    public ITopicKeyStrategy? TopicKeyStrategy { get; set; }

    public TimeSpan? PrimaryMaxExpiration { get; set; }

    public TimeSpan? ConnectionMonitorPeriod { get; set; }

    public bool? UsePrimaryOnlyWhenDisconnected { get; set; }

    public TimeSpan? PrimaryMaxExpirationDisconnected { get; set; }

}
