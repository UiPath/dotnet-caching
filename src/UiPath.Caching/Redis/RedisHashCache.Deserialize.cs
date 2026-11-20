using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    [SuppressMessage("SonarLint.Rule", "S3776")]
    private ICacheEntry<IDictionary<string, T?>> ParseCacheEntry<T>(CacheKey cacheKey, RedisKey redisKey, HashEntry[] hashEntries, DateTimeOffset? expireTime, ReadableFormats? readable, ref int unreadable)
    {
        if (hashEntries.Length == 0)
        {
            return Default<T>();
        }

        Dictionary<string, T?> values = [];
        IDictionary<string, string?>? extendedProps = default;
        FieldRewrites? rewrites = null;
        var hasEmptyMarker = false;
        long version = 0;

        for (var i = 0; i < hashEntries.Length; i++)
        {
            var hashEntry = hashEntries[i];
            var name = hashEntry.Name;
            var v = hashEntry.Value;
            if (KnownFieldNames.IsVersion(name))
            {
                version = v.TryParse(out long counted) ? counted : 0;
                continue;
            }


            if (KnownFieldNames.IsMetadata(name))
            {
                if (v.Length() == 0)
                {
                    hasEmptyMarker = true;
                }
                else
                {
                    extendedProps = ReadValue<IDictionary<string, string?>>(v, readable, ref unreadable);
                }
                continue;
            }

            values.Add(name.ToString(), DeserializeField<T>(name, v, readable, ref unreadable, ref rewrites));
        }

        if (unreadable != 0)
        {
            return Default<T>();
        }

        OfferRewrites<T>(redisKey, rewrites);

        if (values.Count == 0 && (!_cacheNullValues || !hasEmptyMarker))
        {
            return Default<T>();
        }

        var entry = _cacheEntryFactory.Create<IDictionary<string, T?>>(values, Clock.ToDateTimeOffset(expireTime), extendedProps);
        if (version > 0)
        {
            HashVersions.Stamp<T>(entry, version, null);
        }

        return entry;
    }

    // Bytes that did not come from Redis just now (the write-behind buffer, a peer's event): not counted as a read and never offered.
    private T? DeserializeField<T>(RedisValue value, ReadableFormats? readable, ref int unreadable)
    {
        if (value.IsNull)
        {
            return default;
        }
        if (value.Length() == 0)
        {
            return default;
        }
        var deserialized = ReadValue<T>(value, readable, ref unreadable);
        if (!_cacheNullValues && IsDefault(deserialized))
        {
            return default;
        }
        return deserialized;
    }

    /// <summary>A field a migrating call found in another readable format is noted in <paramref name="rewrites"/>, to be offered once the whole read is known to have hit.</summary>
    private T? DeserializeField<T>(RedisValue name, RedisValue value, ReadableFormats? readable, ref int unreadable, ref FieldRewrites? rewrites)
    {
        if (value.IsNull)
        {
            return default;
        }
        if (value.Length() == 0)
        {
            return default;
        }
        var deserialized = ReadValue<T>(value, readable, ref unreadable, out var sourceFormat);
        if (sourceFormat >= 0 && SerializerMetricsOn && !KnownFieldNames.IsSystemField(name))
        {
            TrackSerializer(Metrics.SerializerRead, _formats.Reader!.NameOf(sourceFormat), MetricCache);
        }

        if (!_cacheNullValues && IsDefault(deserialized))
        {
            return default;
        }

        if (sourceFormat >= 0 && !IsDefault(deserialized) && readable?.MigrateTo is { } target && Migration is { } migration)
        {
            var stored = (byte[]?)value;
            if (migration.Wants(target, stored!))
            {
                (rewrites ??= new(target)).TryAdd(name, stored!);
            }
        }

        return deserialized;
    }

    /// <summary>The value by its format header when the call sniffs, else through the proxy; a header no serializer owns, or the call's formats leave out, sets <paramref name="unreadable"/> to its id, and a serializer that throws to <see cref="SerializerFailed"/>; both read as nothing.</summary>
    private T? ReadValue<T>(RedisValue value, ReadableFormats? readable, ref int unreadable) =>
        ReadValue<T>(value, readable, ref unreadable, out _);

    /// <param name="sourceFormat">The format id the value was stored under, 0 for JSON; -1 when the call does not look for one, or the value was a miss.</param>
    private T? ReadValue<T>(RedisValue value, ReadableFormats? readable, ref int unreadable, out int sourceFormat)
    {
        sourceFormat = -1;
        if (readable is null)
        {
            return _serializer.Deserialize<T>(value);
        }

        var outcome = _formats.Reader!.Read<T>(((byte[]?)value)!, readable);
        if (outcome.IsMiss)
        {
            if (outcome.Failure is { } failure)
            {
                LogRedisHashCacheException(failure);
                unreadable = unreadable == 0 ? SerializerFailed : unreadable;
            }
            else
            {
                unreadable = outcome.UnreadableFormat != 0 ? outcome.UnreadableFormat : outcome.ExcludedFormat;
            }

            return default;
        }

        sourceFormat = outcome.SourceFormat;
        return outcome.Value;
    }

    private ValueFormat FormatFor(CachePolicy? policy, bool forWrite) => _formats.For(policy, forWrite);

    // The hash is left alone: a node that cannot read a format may be a stale one, and removing one field would leave the rest looking like a whole hash.
    private void TrackUnreadableRead(int unreadable)
    {
        if (unreadable is not 0 and not SerializerFailed)
        {
            TrackUnreadable(unreadable, Name);
        }
    }

    private bool IsCacheHit(RedisValue value) =>
        !value.IsNull && (value.Length() != 0 || _cacheNullValues);
}
