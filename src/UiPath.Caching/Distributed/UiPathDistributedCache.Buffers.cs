#if NET9_0_OR_GREATER
using System.Buffers;
using Microsoft.Extensions.Caching.Distributed;

namespace UiPath.Caching.Distributed;

/// <summary>Compiled only where <c>IBufferDistributedCache</c> exists: the <c>net8.0</c> floor pins <c>Microsoft.Extensions.Caching.Abstractions</c> to 8.0.0, which predates it.</summary>
internal sealed partial class UiPathDistributedCache : IBufferDistributedCache
{
    public bool TryGet(string key, IBufferWriter<byte> destination) =>
        TryGetAsync(key, destination).AsTask().GetAwaiter().GetResult();

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

    /// <summary>The sequence is borrowed and reclaimed by the caller on return, so a tier that retains values gets a copy.</summary>
    public async ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        if (TierRetainsValues)
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
