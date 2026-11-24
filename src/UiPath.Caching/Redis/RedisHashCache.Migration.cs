using StackExchange.Redis;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache : IMigrationHashCache
{
    public async ValueTask<HashSnapshotRead<T>> ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        try
        {
            // The read of GetCacheEntryAsync in its atomic form: the fields and the lifetime in one transaction on the master, and errors are not swallowed.
            var entry = format.Sniff
                ? await GetInnerCacheEntryAsync<T, ValueFormat>(cacheKey, format, token, atomic: true).ConfigureAwait(false)
                : await GetInnerCacheEntryAsync<T, PlainFormat>(cacheKey, default, token, atomic: true).ConfigureAwait(false);
            return entry switch
            {
                UnreadableEntry<IDictionary<string, T?>> => new HashSnapshotRead<T>(SnapshotOutcome.Unreadable, null),
                { Found: true } => new HashSnapshotRead<T>(SnapshotOutcome.Found, entry),
                _ => new HashSnapshotRead<T>(SnapshotOutcome.Absent, null),
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRedisHashCacheException(ex);
            return default;
        }
    }

    public async ValueTask<StoreOutcome> WriteAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        var trace = new SendTrace();
        ValueTask<bool> write;
        try
        {
            if (token.IsCancellationRequested || !IsConnected)
            {
                return StoreOutcome.NotSent;
            }

            // What can throw (the key, the fields, the serializer) runs here, before any byte is sent.
            write = SetCountingAsync(cacheKey, values, options, policy, report: null, token, trace);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRedisHashCacheException(ex);
            return StoreOutcome.NotSent;
        }

        return await OutcomeOfAsync(write, trace, token).ConfigureAwait(false);
    }

    public async ValueTask<StoreOutcome> DeleteAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        var trace = new SendTrace();
        RedisKey redisKey;
        try
        {
            NotCacheableException.ThrowIfNotCacheable<T>();
            if (token.IsCancellationRequested || !IsConnected)
            {
                return StoreOutcome.NotSent;
            }

            redisKey = ToRedisKey(cacheKey, token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRedisHashCacheException(ex);
            return StoreOutcome.NotSent;
        }

        NoteWritten(redisKey);
        try
        {
            return await OutcomeOfAsync(DeleteKeyAsync(redisKey, token, trace), trace, token).ConfigureAwait(false);
        }
        finally
        {
            NoteWritten(redisKey);
        }
    }

    public async ValueTask<StoreOutcome> DeleteFieldsAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default)
    {
        var trace = new SendTrace();
        RedisKey redisKey;
        try
        {
            NotCacheableException.ThrowIfNotCacheable<T>();
            HashFields.ValidateFieldsForWrite(fields);
            if (token.IsCancellationRequested || !IsConnected)
            {
                return StoreOutcome.NotSent;
            }

            redisKey = ToRedisKey(cacheKey, token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRedisHashCacheException(ex);
            return StoreOutcome.NotSent;
        }

        if (fields.Length == 0)
        {
            return StoreOutcome.Committed;
        }

        // The chunks go out together, so the delete is one send that is answered when every chunk is.
        return await OutcomeOfAsync(DeleteFieldsTracedAsync(redisKey, fields, trace, token), trace, token).ConfigureAwait(false);
    }

    public async ValueTask<StoreOutcome> RenewAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        var trace = new SendTrace();
        ValueTask<bool> refresh;
        try
        {
            NotCacheableException.ThrowIfNotCacheable<T>();
            if (token.IsCancellationRequested || !IsConnected)
            {
                return StoreOutcome.NotSent;
            }

            // The refresh of the options, as the library makes it, on a hash that is there: it clears the metadata the options leave out, and it removes or sets the lifetime. A hash that is not there is left as it is.
            refresh = RefreshWithOptionsAsync<T>(cacheKey, options, policy, FormatFor(policy, forWrite: true).Writer, token, trace, requireKey: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRedisHashCacheException(ex);
            return StoreOutcome.NotSent;
        }

        return await OutcomeOfAsync(refresh, trace, token).ConfigureAwait(false);
    }

    private static async ValueTask<StoreOutcome> OutcomeOfAsync(ValueTask<bool> command, SendTrace trace, CancellationToken token)
    {
        try
        {
            await command.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Whatever was sent is judged by what was answered.
        }

        return trace.Outcome();
    }

    private async ValueTask<bool> DeleteFieldsTracedAsync(RedisKey redisKey, string[] fields, SendTrace trace, CancellationToken token)
    {
        try
        {
            trace.MarkSent();
            // The chunks are not issued through the write pipeline, so a hung server is told apart from a refused delete by the caller's token.
            await DeleteFieldsAsync(redisKey, fields, versioned: false, token).AsTask().WaitAsync(token).ConfigureAwait(false);
            trace.MarkAnswered(true);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRedisHashCacheException(ex);
            return false;
        }
    }
}
