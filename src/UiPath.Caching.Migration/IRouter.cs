namespace UiPath.Caching.Migration;

public interface IRouter
{
    IRoutedCache<T> Get<T>(string cacheName)
        where T : class;
}
