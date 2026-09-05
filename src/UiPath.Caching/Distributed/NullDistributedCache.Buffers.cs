#if NET9_0_OR_GREATER
using System.Buffers;
using Microsoft.Extensions.Caching.Distributed;

namespace UiPath.Caching.Distributed;

/// <summary>
/// Kept in step with <see cref="UiPathDistributedCache"/>, so turning caching off does not change which
/// half of the contract a consumer discovers — only whether it finds anything in the cache.
/// </summary>
internal sealed partial class NullDistributedCache : IBufferDistributedCache
{
    public bool TryGet(string key, IBufferWriter<byte> destination) => false;

    public ValueTask<bool> TryGetAsync(string key, IBufferWriter<byte> destination, CancellationToken token = default) =>
        new(false);

    public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options)
    {
    }

    public ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        default;
}
#endif
