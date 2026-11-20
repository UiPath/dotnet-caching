using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    public bool TryDecodeFields<T>(IReadOnlyDictionary<string, byte[]?> encoded, CachePolicy? policy, [MaybeNullWhen(false)] out Dictionary<string, T?> values)
    {
        try
        {
            // The held key's own policy decides the gate, as it does for a read. Where serializers are registered and that policy is not known, nothing is decoded.
            var readable = FormatFor(policy, forWrite: false).Readable;
            // With no policy, the default's own Read list would stand in for the key's, so only an unlimited one decodes.
            var ambiguous = _formats.Reader is not null && (readable is null || (policy is null && !ReferenceEquals(readable, ReadableFormats.Any)));
            var unreadable = 0;
            var decoded = new Dictionary<string, T?>(encoded.Count);
            foreach (var (field, bytes) in encoded)
            {
                if (KnownFieldNames.IsReserved(field))
                {
                    values = null;
                    return false;
                }

                if (ambiguous && bytes is { Length: > 0 })
                {
                    values = null;
                    return false;
                }

                decoded[field] = DeserializeField<T>(bytes is null ? RedisValue.Null : (byte[])bytes.Clone(), readable, ref unreadable);
                if (unreadable != 0)
                {
                    values = null;
                    return false;
                }
            }

            values = decoded;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            values = null;
            return false;
        }
    }

    private static DateTimeOffset? ToExpiration(long expireAt) =>
        // PEXPIRETIME answers -1 for a key without a TTL and -2 for a missing one.
        expireAt < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(expireAt);

    private static (HashEntry[] Entries, DateTimeOffset? Expiration) ReadReply(RedisResult reply)
    {
        var parts = ((RedisResult[]?)reply)!;
        var fields = ((RedisValue[]?)parts[0])!;
        var entries = new HashEntry[fields.Length / 2];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = new HashEntry(fields[2 * i], fields[(2 * i) + 1]);
        }

        return (entries, ToExpiration((long)parts[1]));
    }

    private async ValueTask<T?> GetItemCoreAsync<T, TFormat>(CacheKey cacheKey, string field, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        HashFields.ValidateFieldForRead(field);
        return await GetInnerAsync<T?, TFormat>(cacheKey, field, format, token);
    }

    private async ValueTask<IDictionary<string, T?>> GetCoreAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await GetInnerAsync<T?, TFormat>(cacheKey, format, token);
    }

    private async ValueTask<IDictionary<string, T?>> GetFieldsCoreAsync<T, TFormat>(CacheKey cacheKey, string[] fields, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await GetInnerAsync<T?, TFormat>(cacheKey, fields, format, token);
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryCoreAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return await GetInnerCacheEntryAsync<T?, TFormat>(cacheKey, format, token);
    }

    /// <summary>The hash, or its length when it is too large for one command.</summary>
    private async ValueTask<(HashEntry[] Entries, DateTimeOffset? Expiration)> ParseReplyAsync(RedisKey redisKey, RedisResult reply)
    {
        var parts = ((RedisResult[]?)reply)!;
        if (parts[0].Resp2Type != ResultType.Integer)
        {
            return ReadReply(reply);
        }

        // The pages are not one snapshot: a write between two of them can show in one and not the other.
        var fields = new Dictionary<RedisValue, RedisValue>();
        await foreach (var entry in Database.HashScanAsync(redisKey, pageSize: _fieldBatchSize, flags: ReadFlagsFor(redisKey)).ConfigureAwait(false))
        {
            // The pages are not one snapshot, so a version read among them says nothing of the fields read before it.
            if (!KnownFieldNames.IsVersion(entry.Name))
            {
                fields[entry.Name] = entry.Value;
            }
        }

        return (fields.Select(f => new HashEntry(f.Key, f.Value)).ToArray(), ToExpiration((long)parts[1]));
    }

    /// <summary>One HGETALL, or where scripts run, the script that pages a large hash.</summary>
    private async ValueTask<HashEntry[]> ReadAllEntriesAsync(RedisKey redisKey)
    {
        if (await TryReadScriptAsync(ReadWithExpirationScript, [redisKey], _logReadScriptUnavailable, [(long)_fieldBatchSize * PagedReadFactor]).ConfigureAwait(false) is { } reply)
        {
            return (await ParseReplyAsync(redisKey, reply).ConfigureAwait(false)).Entries;
        }

        return await Database.HashGetAllAsync(redisKey, ReadFlagsFor(redisKey)).ConfigureAwait(false);
    }

    private async ValueTask<RedisValue[]> HashGetFieldsAsync(RedisKey redisKey, string[] fields)
    {
        if (fields.Length <= _fieldBatchSize)
        {
            return await Database.HashGetAsync(redisKey, fields.Select(k => (RedisValue)k).ToArray(), ReadFlagsFor(redisKey)).ConfigureAwait(false);
        }

        var flags = ReadFlagsFor(redisKey);
        var pending = new List<Task<RedisValue[]>>();
        for (var start = 0; start < fields.Length; start += _fieldBatchSize)
        {
            var chunk = new RedisValue[Math.Min(_fieldBatchSize, fields.Length - start)];
            for (var i = 0; i < chunk.Length; i++)
            {
                chunk[i] = fields[start + i];
            }

            pending.Add(Database.HashGetAsync(redisKey, chunk, flags));
        }

        return (await Task.WhenAll(pending).ConfigureAwait(false)).SelectMany(values => values).ToArray();
    }

    /// <summary>The hash's user fields, and whether they count as a hit: a value other than the null marker, or the empty marker while nulls are cached.</summary>
    private (Dictionary<string, T?> Values, bool Found) ReadFields<T>(HashEntry[] hashEntries, ReadableFormats? readable, ref int unreadable, ref FieldRewrites? rewrites)
    {
        var values = new Dictionary<string, T?>();
        var hasEmptyMarker = false;
        var anyValue = false;
        foreach (var hashEntry in hashEntries)
        {
            if (KnownFieldNames.IsMetadata(hashEntry.Name))
            {
                hasEmptyMarker = hashEntry.Value.Length() == 0;
                continue;
            }
            if (KnownFieldNames.IsSystemField(hashEntry.Name))
            {
                continue;
            }
            var v = hashEntry.Value;
            anyValue |= IsCacheHit(v);
            values.Add(hashEntry.Name.ToString(), DeserializeField<T>(hashEntry.Name, v, readable, ref unreadable, ref rewrites));
        }

        if (unreadable != 0)
        {
            return (new Dictionary<string, T?>(), false);
        }

        return (values, anyValue || (hasEmptyMarker && _cacheNullValues));
    }
}
