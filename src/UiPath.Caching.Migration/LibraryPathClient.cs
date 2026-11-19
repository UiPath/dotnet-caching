namespace UiPath.Caching.Migration;

/// <summary>The library path: a failed snapshot read is unavailable and never absent, a write refused before sending is not sent, and any other failed write is unknown and never replayed.</summary>
internal sealed class LibraryPathClient<T>(ILibraryStore store, IConnectionState connection, Func<string, string> redisKey, Func<TimeSpan> settleWithin) : IPathClient<T>
    where T : class
{
    public LibraryPathClient(ILibraryStore store, IConnectionState connection, Func<string, string> redisKey, TimeSpan settleWithin)
        : this(store, connection, redisKey, () => settleWithin)
    {
    }

    public TimeSpan? SettleWithin => settleWithin();

    public async ValueTask<PathRead<T>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        if (!connection.IsConnected)
        {
            return PathRead<T>.Of(ReadOutcome.Unavailable);
        }

        var probe = ReadProbe.Begin();
        try
        {
            var entry = await store.ReadSnapshotAsync<T>(CacheKey.Verbatim(redisKey(key)), cancellationToken).ConfigureAwait(false);
            if (entry.Found && entry.Value is { } value)
            {
                return new PathRead<T>(ReadOutcome.Found, value, entry.Expiration == DateTimeOffset.MaxValue ? null : entry.Expiration);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return PathRead<T>.Of(probe.Unreadable ? ReadOutcome.Unreadable : ReadOutcome.Unavailable);
        }
        finally
        {
            ReadProbe.End();
        }

        return PathRead<T>.Of(probe.Unreadable ? ReadOutcome.Unreadable : ReadOutcome.Absent);
    }

    public ValueTask<WriteResult> SetAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken) =>
        WriteAsync(key, (cacheKey, token) => store.SetAsync(cacheKey, value, deadline ?? DateTimeOffset.MaxValue, token), cancellationToken);

    public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken) =>
        WriteAsync(key, (cacheKey, token) => store.RemoveAsync<T>(cacheKey, token), cancellationToken);

    private static WriteResult Failed(ReadProbe probe, TimeSpan bound) =>
        probe.SerializeFailed ? WriteOutcome.NotSent : WriteResult.UnknownWithin(bound);

    private async ValueTask<WriteResult> WriteAsync(string key, Func<CacheKey, CancellationToken, ValueTask<bool>> command, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !connection.IsConnected)
        {
            return WriteOutcome.NotSent;
        }

        CacheKey cacheKey;
        try
        {
            cacheKey = CacheKey.Verbatim(redisKey(key));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return WriteOutcome.NotSent;
        }

        var bound = settleWithin();
        var probe = ReadProbe.Begin();
        try
        {
            return await command(cacheKey, cancellationToken).ConfigureAwait(false)
                ? WriteOutcome.Committed
                : Failed(probe, bound);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Failed(probe, bound);
        }
        finally
        {
            ReadProbe.End();
        }
    }
}
