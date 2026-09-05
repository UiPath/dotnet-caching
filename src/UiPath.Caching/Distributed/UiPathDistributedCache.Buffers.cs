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

        destination.Write(Payload(fields).Span);
        return true;
    }

    public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options) =>
        SetAsync(key, value, options).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// The sequence is borrowed: a pooled caller reclaims it the moment this returns. Where the tier keeps
    /// what it is handed, it is copied into an owned array first. Elsewhere a single segment goes straight
    /// through — Redis copies it into the connection's buffer as the command is written, and the write is
    /// awaited to completion — and a segmented sequence is flattened into a rented buffer that is returned
    /// once that await is over.
    /// </summary>
    public async ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        if (_tierRetainsValues)
        {
            await WriteAsync(key, value.ToArray(), options, token).ConfigureAwait(false);
        }
        else if (value.IsSingleSegment)
        {
            await WriteAsync(key, value.First, options, token).ConfigureAwait(false);
        }
        else
        {
            var length = checked((int)value.Length);
            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                value.CopyTo(rented);
                await WriteAsync(key, rented.AsMemory(0, length), options, token).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
#endif
