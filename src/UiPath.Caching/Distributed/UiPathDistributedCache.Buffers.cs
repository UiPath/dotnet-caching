#if NET9_0_OR_GREATER
using System.Buffers;
using Microsoft.Extensions.Caching.Distributed;

namespace UiPath.Caching.Distributed;

/// <summary>
/// The buffer-based half of the contract, added in .NET 9. Callers that already own a buffer —
/// <c>HybridCache</c>, output caching, session state — hand one in instead of trading a fresh byte array
/// per operation, so the payload lands directly in pooled memory. Compiled only where the abstraction
/// exists: the <c>net8.0</c> floor pins <c>Microsoft.Extensions.Caching.Abstractions</c> to 8.0.0, which
/// predates the interface, and consumers discover it with a type check that simply comes up empty there.
/// </summary>
/// <remarks>
/// Both halves route through the same read and write paths, so expiration, sliding, keyspace and logging
/// behave identically no matter which one a caller reaches for.
/// </remarks>
internal sealed partial class UiPathDistributedCache : IBufferDistributedCache
{
    public bool TryGet(string key, IBufferWriter<byte> destination) =>
        TryGetAsync(key, destination).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// A live entry writes its payload and reports <c>true</c>; a miss writes nothing and reports
    /// <c>false</c>. An entry stored with an empty payload is a hit that writes no bytes — the distinction
    /// <see cref="GetAsync"/> cannot express, since a miss and an empty entry both surface as no bytes.
    /// </summary>
    public async ValueTask<bool> TryGetAsync(string key, IBufferWriter<byte> destination, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (await ReadAsync(key, includeData: true, token).ConfigureAwait(false) is not { } fields)
        {
            return false;
        }

        destination.Write(Payload(fields));
        return true;
    }

    public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options) =>
        SetAsync(key, value, options).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// The sequence is copied rather than aliased: the memory-backed tiers keep whatever array they are
    /// handed, and a caller that pooled its buffer returns it as soon as this call completes.
    /// </summary>
    public ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        new(SetAsync(key, value.ToArray(), options, token));
}
#endif
