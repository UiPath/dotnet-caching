namespace UiPath.Caching;

/// <summary>A peer-refreshed entry that the cache's local reads check before they use it.</summary>
internal interface IDeferredEntry
{
    /// <summary>Deserializes the payload unless a read has; false when it holds no value the cache keeps. <paramref name="first"/> is true for one caller only, and <paramref name="threw"/> when the serializer threw.</summary>
    bool Holds(out bool first, out bool threw);
}
