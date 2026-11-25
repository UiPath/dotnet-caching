using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>Binds each registered key move cache to a codec per side.</summary>
/// <param name="inPlaceKeepsSystemFields">Whether the hashes of the in-place side keep the system fields of the library's format (the metadata, the write count and the empty marker). False where the in-place side is the older client's format, which reads none of them.</param>
internal sealed class KeyMoveBinder(IByteCodecCache inPlace, IByteCodecCache native, IReadOnlyList<KeyMoveCache> caches, KeyMoveOptions? options = null, KeyMoveValueClasses? classes = null, IByteHashCodecCache? inPlaceHash = null, IByteHashCodecCache? nativeHash = null, bool inPlaceKeepsSystemFields = false, Func<string, WriteLease>? inPlacePolicy = null)
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

        var binding = new KeyMoveBinding<T>(new LibraryKeyMoveCodec<T>(inPlace, _options.MaxValueBytes, InPlacePolicyOf(cache.Group)), new LibraryKeyMoveCodec<T>(native, _options.MaxValueBytes), cache.FormatInPlace, cache.FormatNative, cache.NoOrigin) { Options = _options };
        Register(name, binding);
        return binding;
    }

    /// <summary>The hash binding of the cache named <paramref name="name"/>, or null when it is not a key move cache.</summary>
    /// <param name="maxHashFields">The registration's field cap, or null for the option's default.</param>
    /// <exception cref="InvalidOperationException">The cache is a key move cache and the host has no hash cache to take the codecs from: the startup of the key move fails, as it does for a missing string codec cache.</exception>
    public KeyMoveHashBinding<T>? BindHash<T>(string name, int? maxHashFields = null)
        where T : class
    {
        if (caches.FirstOrDefault(c => c.Name == name) is not { } cache)
        {
            return null;
        }

        if (inPlaceHash is null || nativeHash is null)
        {
            throw new InvalidOperationException($"The key move cache '{name}' holds hashes: it needs the router's own Redis hash cache for the in-place format and the application's Redis hash cache for the native format, so enable the Redis cache provider for both.");
        }

        var fields = maxHashFields ?? _options.MaxHashFields;
        if (fields < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHashFields), fields, "A hash that may be promoted has at least one field.");
        }

        var binding = new KeyMoveHashBinding<T>(
            // The in-place side is the older client's format when the router was given an envelope: its readers know no system fields, so none is written to L. Only a move between library formats carries them.
            new LibraryKeyMoveHashCodec<T>(inPlaceHash, _options.MaxValueBytes, fields, systemFields: inPlaceKeepsSystemFields, policy: InPlacePolicyOf(cache.Group)),
            new LibraryKeyMoveHashCodec<T>(nativeHash, _options.MaxValueBytes, fields, systemFields: true),
            cache.FormatInPlace,
            cache.FormatNative,
            cache.NoOrigin,
            fields) { Options = _options };
        RegisterHash(name, binding);
        return binding;
    }

    /// <summary>Tells the inventory how to judge the values of a cache, for a binding that did not come from <see cref="Bind{T}"/> too.</summary>
    public void Register<T>(string name, KeyMoveBinding<T> binding)
        where T : class => classes?.Add(name, binding);

    /// <summary>Tells the inventory how to judge the hashes of a cache, for a binding that did not come from <see cref="BindHash{T}"/> too.</summary>
    public void RegisterHash<T>(string name, KeyMoveHashBinding<T> binding)
        where T : class => classes?.AddHash(name, binding);

    // L is written in the format the group writes now: the envelope before its switch, MessagePack after it.
    private Func<WriteLease>? InPlacePolicyOf(string group) => inPlacePolicy is { } policy ? () => policy(group) : null;
}
