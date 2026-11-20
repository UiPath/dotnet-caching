#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

/// <remarks>
/// <para><b>Expiration.</b> The <c>expiration</c> parameter is not nullable. A caller with nothing to
/// say about lifetime calls the overload that has no <c>expiration</c> parameter and gets
/// <see cref="CachePolicy.DistributedExpiration"/>, then the cache default; a caller that passes one
/// means it, so a duration that is not positive — or a deadline that has already passed — is
/// rejected with <see cref="ArgumentOutOfRangeException"/> rather than silently ignored. See
/// <c>CacheExpiration</c>. The no-op implementations in this package read no argument at all
/// and so enforce nothing.</para>
/// <para><b>Batch GetOrAdd.</b> An implementation outside the library can forward the batch
/// <c>GetOrAddAsync</c> overloads to <c>BatchGetOrAdd.RunAsync</c> in <c>UiPath.Caching</c>.</para>
/// </remarks>
public partial interface ICache : IDisposable
{
    string Name { get; }

    [OverloadResolutionPriority(1)]
    ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);
}
