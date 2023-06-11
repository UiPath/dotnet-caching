namespace UiPath.Platform.Caching.Broadcast;

public interface ITopicFactory
{
    ITopicProvider Get(string? providerName, Type entryType);

    void AddProvider(ITopicProvider provider);
}
