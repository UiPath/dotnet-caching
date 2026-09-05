using Microsoft.Extensions.Caching.Distributed;

namespace UiPath.Caching.Distributed;

/// <summary>
/// Stand-in used when caching is disabled, so consumers such as <c>AddSession()</c> and DataProtection
/// still resolve <see cref="IDistributedCache"/> instead of failing at startup. Reads always miss.
/// </summary>
internal sealed partial class NullDistributedCache : IDistributedCache
{
    public static readonly NullDistributedCache Instance = new();

    private NullDistributedCache()
    {
    }

    public byte[]? Get(string key) => null;

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);

    public void Refresh(string key)
    {
    }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key)
    {
    }

    public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Task.CompletedTask;
}
