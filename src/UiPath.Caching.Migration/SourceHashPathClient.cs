namespace UiPath.Caching.Migration;

internal sealed class SourceHashPathClient<T>(ISourceHashCache<T> source, string cacheName) : IHashPathClient<T>
    where T : class
{
    public TimeSpan? SettleWithin => null;

    public async ValueTask<HashRead<T>> ReadAsync(string key, string[]? fields, CancellationToken cancellationToken)
    {
        SourceRead<IDictionary<string, T?>> read;
        try
        {
            read = await source.GetAsync(key, fields, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HashRead<T>.Of(ReadOutcome.Unavailable);
        }

        if (read.Kind != ReadOutcome.Found)
        {
            return HashRead<T>.Of(read.Kind);
        }

        if (read.Value is not { Count: > 0 } value)
        {
            return HashRead<T>.Of(ReadOutcome.Absent);
        }

        var found = new HashRead<T>(ReadOutcome.Found, value, null);
        return fields is null ? found : found.Only(fields);
    }

    public ValueTask<bool?> ContainsAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<bool?>(null);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"The source of hash cache '{cacheName}' reports no expiry.");

    public async ValueTask<WriteResult> SetAsync(string key, IDictionary<string, T?> fields, HashWriteOptions options, CancellationToken cancellationToken)
    {
        if (options.Metadata is not null)
        {
            throw new NotSupportedException($"The source of hash cache '{cacheName}' cannot store metadata, so a write that carries it is refused.");
        }

        try
        {
            return await source.SetAsync(key, fields, options.Mode, CacheExpiry.At(options.Deadline ?? DateTimeOffset.MaxValue), cancellationToken).ConfigureAwait(false);
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

    public async ValueTask<WriteResult> RemoveFieldsAsync(string key, string[] fields, CancellationToken cancellationToken)
    {
        try
        {
            return await source.RemoveFieldsAsync(key, fields, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return WriteOutcome.Unknown;
        }
    }

    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"The source of hash cache '{cacheName}' has no refresh.");

    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, IDictionary<string, string?>? metadata, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"The source of hash cache '{cacheName}' has no refresh.");

    /// <summary>The source has no conditional write, so its fill is its replace.</summary>
    public ValueTask<WriteResult> FillAsync(string key, IDictionary<string, T?> fields, HashWriteOptions options, HashFillBox<T>? stored, CancellationToken cancellationToken) =>
        SetAsync(key, fields, options with { Mode = HashCacheSetOption.KeyReplace }, cancellationToken);
}
