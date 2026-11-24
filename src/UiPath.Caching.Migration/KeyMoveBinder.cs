using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>Binds each registered key move cache to a codec per side.</summary>
internal sealed class KeyMoveBinder(IByteCodecCache inPlace, IByteCodecCache native, IReadOnlyList<KeyMoveCache> caches, KeyMoveOptions? options = null, KeyMoveValueClasses? classes = null)
{
    private readonly KeyMoveOptions _options = options ?? new();

    /// <summary>The binding of the cache named <paramref name="name"/>, or null when it is not a key move cache.</summary>
    public KeyMoveBinding<T>? Bind<T>(string name)
        where T : class
    {
        if (caches.FirstOrDefault(c => c.Name == name) is not { } cache)
        {
            return null;
        }

        var binding = new KeyMoveBinding<T>(new LibraryKeyMoveCodec<T>(inPlace, _options.MaxValueBytes), new LibraryKeyMoveCodec<T>(native, _options.MaxValueBytes), cache.FormatInPlace, cache.FormatNative, cache.NoOrigin) { Options = _options };
        Register(name, binding);
        return binding;
    }

    /// <summary>Tells the inventory how to judge the values of a cache, for a binding that did not come from <see cref="Bind{T}"/> too.</summary>
    public void Register<T>(string name, KeyMoveBinding<T> binding)
        where T : class => classes?.Add(name, binding);
}
