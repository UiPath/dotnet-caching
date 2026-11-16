using System.Buffers;

namespace UiPath.Caching;

/// <summary>Writes and reads cache values as bytes, each tagged on the wire with one of its <see cref="FormatIds"/>.</summary>
/// <remarks>
/// One instance serves every thread and every cache at once, so an implementation must be thread-safe and keep no state between calls; calls may be nested on one thread.
/// The library writes and strips the format header: <see cref="Serialize{T}"/> writes none and <see cref="Deserialize{T}"/> receives header-free bytes.
/// </remarks>
public interface ICacheValueSerializer
{
    /// <summary>The ids this serializer writes and reads, all with the same header length: one byte for 0x01-0x1E, two for 0x100-0x1FF.</summary>
    /// <remarks>Must equal, as a set, the ids declared when the serializer was registered, and must not change afterwards; a mismatch fails when the registry is built.</remarks>
    IReadOnlyList<int> FormatIds { get; }

    /// <summary>Writes at least one byte and returns the id it wrote, one of <see cref="FormatIds"/>.</summary>
    /// <remarks>
    /// A null value included: an empty payload is reserved for a cached null. <paramref name="target"/> is valid only during the call.
    /// Must be deterministic: <c>MigrateOnRead</c> rewrites a value only when <c>Deserialize(Serialize(x))</c> is stable, and counts one that is not as lossy and skips it.
    /// </remarks>
    int Serialize<T>(T value, IBufferWriter<byte> target);

    /// <summary>Returns the value in <paramref name="source"/>, or throws; must not retain <paramref name="source"/> past the call.</summary>
    /// <remarks>
    /// Throw on an id it does not own and on corrupt or truncated input, and never return a partial value: the reader maps a throw to a cache miss.
    /// <paramref name="source"/> may span several segments or be empty. A null result for a non-empty payload is read as a cached null: a hit when <c>CacheNullValues</c> is on, a miss when it is off.
    /// </remarks>
    T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source);
}
