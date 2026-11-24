using System.Collections.Concurrent;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The codec's verdict on stored values for the inventory, filled in as each cache is bound.</summary>
internal sealed class KeyMoveValueClasses : IValueClassifier
{
    private readonly ConcurrentDictionary<string, Func<MoveSide, long, bool>> _caches = new(StringComparer.Ordinal);

    /// <summary>A null the other side does not cache, or a value too large for a cache that has an origin to reload it.</summary>
    public static bool Droppable(DecodeClass decoded, bool noOrigin, bool otherCachesNulls) =>
        decoded switch
        {
            DecodeClass.Null => !otherCachesNulls,
            DecodeClass.TooLarge => !noOrigin,
            _ => false,
        };

    // The stored length decides, as it does in the codec: an empty value is a cached null, and one above the cap is too large.
    public void Add<T>(string cacheId, KeyMoveBinding<T> binding)
        where T : class =>
        _caches[cacheId] = (side, length) => Droppable(
            Classify(length, binding.Options.MaxValueBytes),
            binding.NoOrigin,
            (side == MoveSide.Native ? binding.InPlace : binding.Native).EncodeNull() is not null);

    // The field count decides, as it does in the hash codec and in the read: the inventory asks for the HLEN and never reads a hash.
    public void AddHash<T>(string cacheId, KeyMoveHashBinding<T> binding)
        where T : class =>
        _caches[cacheId] = (side, length) => Droppable(
            KeyMoveHashSize.OfLength(length, binding.MaxHashFields),
            binding.NoOrigin,
            (side == MoveSide.Native ? binding.InPlace : binding.Native).CachesNulls);

    public bool IsDroppable(string cacheId, MoveSide side, long length) =>
        _caches.TryGetValue(cacheId, out var verdict) && verdict(side, length);

    private static DecodeClass Classify(long length, long maxValueBytes)
    {
        if (length == 0)
        {
            return DecodeClass.Null;
        }

        return length > maxValueBytes ? DecodeClass.TooLarge : DecodeClass.Ok;
    }
}
