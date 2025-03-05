namespace UiPath.Platform.Caching.Broadcast;

[ExcludeFromCodeCoverage]
public class NullTopicFactory : ITopicFactory
{
    public static readonly NullTopicFactory Instance = new NullTopicFactory();

    private NullTopicFactory()
    {
    }

    public IEnumerable<string> ProviderNames => [];

    public void AddProvider(ITopicProvider provider)
    {
        // no op
    }

    public ITopicProvider Get(string? providerName = null) =>
        NullTopicProvider.Instance;
}
