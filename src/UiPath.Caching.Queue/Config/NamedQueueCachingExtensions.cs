using UiPath.Caching.Config;

namespace UiPath.Caching.Queue.Config;

public static class NamedQueueCachingExtensions
{
    /// <summary>Re-exposes the stack's <see cref="IQueueCacheFactory"/>, <see cref="ISetCache"/> and <see cref="ISetCache{T}"/> under its name.</summary>
    public static INamedCaching ExposeQueueCaches(this INamedCaching named)
    {
        ArgumentNullException.ThrowIfNull(named);
        named.Expose<IQueueCacheFactory>().Expose<ISetCache>();
        named.Services.AddKeyedTransient(typeof(ISetCache<>), named.Name, typeof(NamedSetCache<>));
        return named;
    }
}
