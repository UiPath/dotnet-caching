using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    private async ValueTask<T?> GetInnerAsync<T, TFormat>(CacheKey cacheKey, string field, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var redisKey = ToRedisKey(cacheKey, token);
        if (!IsConnected)
        {
            return default;
        }
        T? ret = default;
        bool found = false;
        var operation = StartOperation<T>(nameof(GetAsync));
        try
        {
            var value = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.HashGetAsync(s.Key, s.Field, s.Self.ReadFlagsFor(s.Key)).AsValueTask();
            },
            (Self: this, Key: redisKey, Field: field),
            RedisValue.Null,
            token).ConfigureAwait(false);
            var unreadable = 0;
            FieldRewrites? rewrites = null;
            ret = DeserializeField<T?>(field, value, format.Readable, ref unreadable, ref rewrites);
            found = unreadable == 0 && IsCacheHit(value);
            if (unreadable == 0)
            {
                OfferRewrites<T>(redisKey, rewrites);
            }

            TrackUnreadableRead(unreadable);
            operation.Stop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }

        return ret;
    }

    private async ValueTask<IDictionary<string, T?>> GetInnerAsync<T, TFormat>(CacheKey cacheKey, string[] fields, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        if (fields == null || fields.Length == 0)
        {
            return await GetInnerAsync<T, TFormat>(cacheKey, format, token);
        }

        HashFields.ValidateFieldsForRead(fields);
        var redisKey = ToRedisKey(cacheKey, token);

        IDictionary<string, T?> ret = Empty<T?>();
        if (!IsConnected)
        {
            return ret;
        }
        var operation = StartOperation<T>(nameof(GetInnerAsync));
        bool found = false;
        try
        {
            var values = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.HashGetFieldsAsync(s.Key, s.Fields);
            },
            (Self: this, Key: redisKey, Fields: fields),
            [],
            token).ConfigureAwait(false);
            if (values.Length == fields.Length)
            {
                var unreadable = 0;
                var dict = new Dictionary<string, T?>(fields.Length);
                var anyPresent = false;
                FieldRewrites? rewrites = null;
                for (var i = 0; i < fields.Length; i++)
                {
                    var v = values[i];
                    dict.Add(fields[i], DeserializeField<T?>(fields[i], v, format.Readable, ref unreadable, ref rewrites));
                    anyPresent |= IsCacheHit(v);
                }

                if (unreadable == 0)
                {
                    ret = dict;
                    found = anyPresent;
                    OfferRewrites<T>(redisKey, rewrites);
                }

                TrackUnreadableRead(unreadable);
            }
            operation.Stop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }

        return ret;
    }

    private async ValueTask<IDictionary<string, T?>> GetInnerAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var redisKey = ToRedisKey(cacheKey, token);
        IDictionary<string, T?> ret = Empty<T?>();

        if (!IsConnected)
        {
            return ret;
        }

        var operation = StartOperation<T>(nameof(GetInnerAsync));
        bool found = false;
        try
        {
            var hashEntries = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.ReadAllEntriesAsync(s.Key);
            },
            (Self: this, Key: redisKey),
            [],
            token).ConfigureAwait(false);
            if (hashEntries.Length > 0)
            {
                var unreadable = 0;
                FieldRewrites? rewrites = null;
                (ret, found) = ReadFields<T>(hashEntries, format.Readable, ref unreadable, ref rewrites);
                if (unreadable == 0)
                {
                    OfferRewrites<T>(redisKey, rewrites);
                }

                TrackUnreadableRead(unreadable);
            }
            operation.Stop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }

        return ret;
    }

    private ValueTask<(bool Found, IDictionary<string, T?> Values, bool Unreadable)> GetInnerWithFoundAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token)
    {
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetInnerWithFoundAsync<T, ValueFormat>(cacheKey, format, token) : GetInnerWithFoundAsync<T, PlainFormat>(cacheKey, default, token);
    }

    private async ValueTask<(bool Found, IDictionary<string, T?> Values, bool Unreadable)> GetInnerWithFoundAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var redisKey = ToRedisKey(cacheKey, token);
        if (!IsConnected)
        {
            return (false, Empty<T?>(), false);
        }

        var operation = StartOperation<T>(nameof(GetOrAddAsync));
        bool found = false;
        var unreadable = 0;
        IDictionary<string, T?> ret = Empty<T?>();
        try
        {
            var hashEntries = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.ReadAllEntriesAsync(s.Key);
            },
            (Self: this, Key: redisKey),
            [],
            token).ConfigureAwait(false);

            if (hashEntries.Length == 0)
            {
                operation.Stop();
                return (false, ret, false);
            }

            FieldRewrites? rewrites = null;
            (ret, found) = ReadFields<T>(hashEntries, format.Readable, ref unreadable, ref rewrites);
            if (unreadable == 0)
            {
                OfferRewrites<T>(redisKey, rewrites);
            }

            TrackUnreadableRead(unreadable);
            operation.Stop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }

        return (found, ret, unreadable != 0);
    }

    /// <param name="atomic">Reads the fields and the remaining lifetime in one transaction on the primary, and lets a failure through instead of answering a miss.</param>
    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetInnerCacheEntryAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token, bool atomic = false)
        where TFormat : struct, IValueFormat
    {
        var ret = Default<T>();
        if(!IsConnected)
        {
            return atomic ? throw new InvalidOperationException("The cache is not connected") : ret;
        }

        var redisKey = ToRedisKey(cacheKey, token);
        var operation = StartOperation<T>(nameof(GetInnerCacheEntryAsync));
        try
        {
            ret = await GetCacheEntryForKeyAsync<T?, TFormat>(cacheKey, redisKey, format, token, atomic).ConfigureAwait(false);
            operation.Stop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex) when (!atomic && format.Handles(ex))
        {
            operation.Stop();
            LogRedisHashCacheException(ex);
        }
        finally
        {
            TrackRead(operation, ret.Found, redisKey);
        }

        return ret;
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryForKeyAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, TFormat format, CancellationToken token, bool atomic = false)
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();
        // Per attempt, since a retry cannot re-run a drained transaction; boxed so the pipeline stays bool.
        var read = new StrongBox<StrongBox<(HashEntry[] Entries, DateTimeOffset? Expiration)>?>();
        var committed = await _read.ExecuteAsync(static async (s, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (s.Atomic)
            {
                var transaction = s.Self.Database.CreateTransaction();
                var snapshotEntries = transaction.HashGetAllAsync(s.Key, CommandFlags.DemandMaster);
                var snapshotTimeToLive = transaction.KeyTimeToLiveAsync(s.Key, CommandFlags.DemandMaster);
                if (!await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The read transaction was not committed");
                }

                Interlocked.CompareExchange(ref s.Read.Value, new((await snapshotEntries.ConfigureAwait(false), s.Self.Clock.ToDateTimeOffset(await snapshotTimeToLive.ConfigureAwait(false)))), null);
                return true;
            }

            if (await s.Self.TryReadScriptAsync(ReadWithExpirationScript, [s.Key], s.Self._logReadScriptUnavailable, [(long)s.Self._fieldBatchSize * PagedReadFactor]).ConfigureAwait(false) is { } reply)
            {
                Interlocked.CompareExchange(ref s.Read.Value, new(await s.Self.ParseReplyAsync(s.Key, reply).ConfigureAwait(false)), null);
                return true;
            }

            // An HGETALL with a PTTL in one round trip. Not atomic: a key that expires between them reads as fields without a TTL.
            var flags = s.Self.ReadFlagsFor(s.Key);
            var hashEntriesTask = s.Self.Database.HashGetAllAsync(s.Key, flags);
            var expireTimeToLiveTask = s.Self.Database.KeyTimeToLiveAsync(s.Key, flags);
            try
            {
                var entries = await hashEntriesTask.ConfigureAwait(false);
                var expiration = s.Self.Clock.ToDateTimeOffset(await expireTimeToLiveTask.ConfigureAwait(false));
                Interlocked.CompareExchange(ref s.Read.Value, new((entries, expiration)), null);
            }
            catch
            {
                hashEntriesTask.Forget();
                expireTimeToLiveTask.Forget();
                throw;
            }

            return true;
        },
        (Self: this, Key: redisKey, Read: read, Atomic: atomic),
        default,
        token).ConfigureAwait(false);
        if (!committed || read.Value is not { } result)
        {
            throw new InvalidOperationException("Unable to read from redis");
        }

        var unreadable = 0;
        var entry = ParseCacheEntry<T>(cacheKey, redisKey, result.Value.Entries, result.Value.Expiration, format.Readable, ref unreadable);
        TrackUnreadableRead(unreadable);

        // A layered cache above reads this miss and writes the regenerated hash, and says by it whether a field is left to replace.
        return unreadable != 0 ? new UnreadableEntry<IDictionary<string, T?>>(entry) : entry;
    }
}
