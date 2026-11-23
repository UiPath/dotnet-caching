namespace UiPath.Caching.Migration;

internal sealed class SourcePathClient<T>(ISourceCache<T> source, bool cacheNulls = false) : IPathClient<T>
    where T : class
{
    public TimeSpan? SettleWithin => null;

    public async ValueTask<PathRead<T>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        SourceRead<T> read;
        try
        {
            read = await source.GetAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return PathRead<T>.Of(ReadOutcome.Unavailable);
        }

        if (read.Kind != ReadOutcome.Found)
        {
            return PathRead<T>.Of(read.Kind);
        }

        return read.Value is not null || cacheNulls ? new PathRead<T>(ReadOutcome.Found, read.Value, null) : PathRead<T>.Of(ReadOutcome.Absent);
    }

    public async ValueTask<WriteResult> SetAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        try
        {
            return await source.SetAsync(key, value, deadline is { } at ? CacheExpiry.At(at) : CacheExpiry.Default, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return WriteOutcome.Unknown;
        }
    }

    public async ValueTask<AddResult> TryCreateAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        try
        {
            return await source.TryAddAsync(key, value, deadline is { } at ? CacheExpiry.At(at) : CacheExpiry.Default, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return AddOutcome.Unknown;
        }
    }

    /// <summary>The source has no conditional write, so its fill is its set; no surface member of the cache reaches it (a fill by the library's contract is refused while the source serves).</summary>
    public ValueTask<WriteResult> FillAsync(string key, T value, DateTimeOffset? deadline, FillBox<T>? stored, CancellationToken cancellationToken) => SetAsync(key, value, deadline, cancellationToken);

    public async ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await source.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return WriteOutcome.Unknown;
        }
    }
}
