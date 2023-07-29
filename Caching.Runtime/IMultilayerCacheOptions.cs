namespace UiPath.Platform.Caching.Memory;

public interface IMultilayerCacheOptions : ICacheOptions
{
    public string? Topic { get; set; }

    public ITopicKeyStrategy? TopicKeyStrategy { get; set; }

    public TimeSpan? PrimaryMaxExpiration { get; set; }
}
