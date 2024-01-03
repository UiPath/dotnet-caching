namespace UiPath.Platform.Caching.Broadcast;

public interface ITopicFactory
{
    ITopicProvider Get(string? providerName = null);

    void AddProvider(ITopicProvider provider);
}
