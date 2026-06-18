namespace UiPath.Caching.Broadcast;

public interface ITopicFactory
{
    IEnumerable<string> ProviderNames { get; }

    ITopicProvider Get(string? providerName = null);

    void AddProvider(ITopicProvider provider);
}
