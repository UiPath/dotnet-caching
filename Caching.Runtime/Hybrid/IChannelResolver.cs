namespace UiPath.Platform.Caching.Hybrid;

public interface IChannelResolver
{
    Channel GetFor<T>(string key)
        => GetFor(typeof(T), key);

    Channel GetFor(Type type, string key);
}
