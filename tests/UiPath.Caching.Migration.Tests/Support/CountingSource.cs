namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>The older client of a cache with a count of every call that reached it.</summary>
internal sealed class CountingSource(ISourceCache<List<long>> inner) : ISourceCache<List<long>>
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public ValueTask<SourceRead<List<long>>> GetAsync(string key, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return inner.GetAsync(key, cancellationToken);
    }

    public ValueTask<WriteResult> SetAsync(string key, List<long> value, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return inner.SetAsync(key, value, expiry, cancellationToken);
    }

    public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return inner.RemoveAsync(key, cancellationToken);
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return inner.ClearAsync(cancellationToken);
    }
}
