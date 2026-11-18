namespace UiPath.Caching.Migration;

internal sealed class SourcePathClient<T>(ISourceCache<T> source) : IPathClient<T>
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

        return read.Value is { } value ? new PathRead<T>(ReadOutcome.Found, value, null) : PathRead<T>.Of(ReadOutcome.Absent);
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
