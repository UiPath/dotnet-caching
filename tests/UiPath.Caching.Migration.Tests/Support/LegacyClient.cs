using System.Buffers;
using System.Text.Json;
using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>A value envelope of an older client, {"t":"alias","v":value}, written and read as that client does.</summary>
internal static class LegacyEnvelope
{
    public static string Alias(Type type) => type == typeof(List<long>) ? "ListInt64" : type.Name;

    public static byte[] Write(object value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("t", Alias(value.GetType()));
            writer.WritePropertyName("v");
            JsonSerializer.Serialize(writer, value, value.GetType());
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static T? Read<T>(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return default;
        }

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        return root.GetProperty("t").GetString() == Alias(typeof(T)) ? root.GetProperty("v").Deserialize<T>() : default;
    }
}

internal sealed class LegacyEnvelopeProxy : ISerializerProxy<byte[]>
{
    public byte[]? Serialize(object? value) => value is null ? null : LegacyEnvelope.Write(value);

    public T? Deserialize<T>(byte[]? value) => LegacyEnvelope.Read<T>(value);

    public bool TryDeserialize<T>(string? value, out T? result)
    {
        result = default;
        return false;
    }

    public bool TryDeserialize<T>(object? value, out T? result)
    {
        result = default;
        return false;
    }
}

/// <summary>The older client for one cache: key n:{cache},c:{key}, SET with an absolute expiry, and a clear that scans the cache's pattern.</summary>
internal sealed class LegacySource(IConnectionMultiplexer redis, string cacheName) : ISourceCache<List<long>>
{
    public string RedisKey(string key) => $"n:{cacheName},c:{key}";

    public async ValueTask<SourceRead<List<long>>> GetAsync(string key, CancellationToken cancellationToken)
    {
        RedisValue raw;
        try
        {
            raw = await redis.GetDatabase().StringGetAsync(RedisKey(key)).ConfigureAwait(false);
        }
        catch (RedisException)
        {
            return SourceRead<List<long>>.Unavailable;
        }

        if (raw.IsNull)
        {
            return SourceRead<List<long>>.Absent;
        }

        try
        {
            return LegacyEnvelope.Read<List<long>>((byte[]?)raw) is { } value ? SourceRead<List<long>>.Found(value) : SourceRead<List<long>>.Unreadable;
        }
        catch (JsonException)
        {
            return SourceRead<List<long>>.Unreadable;
        }
    }

    public async ValueTask<WriteResult> SetAsync(string key, List<long> value, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        await WriteAsync(redis.GetDatabase(), RedisKey(key), LegacyEnvelope.Write(value), expiry.Deadline, When.Always).ConfigureAwait(false);
        return WriteOutcome.Committed;
    }

    public async ValueTask<AddResult> TryAddAsync(string key, List<long> value, CacheExpiry expiry, CancellationToken cancellationToken) =>
        await WriteAsync(redis.GetDatabase(), RedisKey(key), LegacyEnvelope.Write(value), expiry.Deadline, When.NotExists).ConfigureAwait(false)
            ? AddOutcome.Added
            : AddOutcome.Exists;

    public async ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        await redis.GetDatabase().KeyDeleteAsync(RedisKey(key)).ConfigureAwait(false);
        return WriteOutcome.Committed;
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        var database = redis.GetDatabase();
        foreach (var endpoint in redis.GetEndPoints())
        {
            var server = redis.GetServer(endpoint);
            if (server.IsReplica)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(pattern: $"n:{cacheName},c:*"))
            {
                await database.KeyDeleteAsync(key).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<bool> WriteAsync(IDatabase database, RedisKey key, RedisValue value, DateTimeOffset? deadline, When when)
    {
        if (deadline is null || deadline == DateTimeOffset.MaxValue)
        {
            return await database.StringSetAsync(key, value, when: when).ConfigureAwait(false);
        }

        object[] arguments = [key, value, "PXAT", deadline.Value.ToUnixTimeMilliseconds()];
        if (when == When.NotExists)
        {
            arguments = [.. arguments, "NX"];
        }

        return !(await database.ExecuteAsync("SET", arguments).ConfigureAwait(false)).IsNull;
    }
}

/// <summary>The older client for one hash cache: key h:{cache},c:{key}, one envelope per field, an absolute expiry, and a clear that scans the cache's pattern.</summary>
internal sealed class LegacyHashSource(IConnectionMultiplexer redis, string cacheName) : ISourceHashCache<List<long>>
{
    public string RedisKey(string key) => $"h:{cacheName},c:{key}";

    public async ValueTask<SourceRead<IDictionary<string, List<long>?>>> GetAsync(string key, string[]? fields, CancellationToken cancellationToken)
    {
        HashEntry[] entries;
        try
        {
            var database = redis.GetDatabase();
            entries = fields is null
                ? await database.HashGetAllAsync(RedisKey(key)).ConfigureAwait(false)
                : [.. (await database.HashGetAsync(RedisKey(key), [.. fields.Select(f => (RedisValue)f)]).ConfigureAwait(false)).Select((value, i) => new HashEntry(fields[i], value)).Where(e => !e.Value.IsNull)];
        }
        catch (RedisException)
        {
            return SourceRead<IDictionary<string, List<long>?>>.Unavailable;
        }

        if (entries.Length == 0)
        {
            return SourceRead<IDictionary<string, List<long>?>>.Absent;
        }

        IDictionary<string, List<long>?> read = entries.ToDictionary(e => e.Name.ToString(), e => LegacyEnvelope.Read<List<long>>((byte[]?)e.Value));
        return SourceRead<IDictionary<string, List<long>?>>.Found(read);
    }

    public async ValueTask<WriteResult> SetAsync(string key, IDictionary<string, List<long>?> fields, HashCacheSetOption mode, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        var database = redis.GetDatabase();
        if (mode == HashCacheSetOption.KeyReplace)
        {
            await database.KeyDeleteAsync(RedisKey(key)).ConfigureAwait(false);
        }

        await database.HashSetAsync(RedisKey(key), [.. fields.Select(f => new HashEntry(f.Key, f.Value is null ? RedisValue.EmptyString : LegacyEnvelope.Write(f.Value)))]).ConfigureAwait(false);
        if (expiry.Deadline == DateTimeOffset.MaxValue)
        {
            await database.KeyPersistAsync(RedisKey(key)).ConfigureAwait(false);
        }
        else if (expiry.Deadline is { } deadline)
        {
            await database.KeyExpireAsync(RedisKey(key), deadline.UtcDateTime).ConfigureAwait(false);
        }
        else if (mode == HashCacheSetOption.KeyReplace)
        {
            await database.KeyPersistAsync(RedisKey(key)).ConfigureAwait(false);
        }

        return WriteOutcome.Committed;
    }

    public async ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        await redis.GetDatabase().KeyDeleteAsync(RedisKey(key)).ConfigureAwait(false);
        return WriteOutcome.Committed;
    }

    public async ValueTask<WriteResult> RemoveFieldsAsync(string key, string[] fields, CancellationToken cancellationToken)
    {
        await redis.GetDatabase().HashDeleteAsync(RedisKey(key), [.. fields.Select(f => (RedisValue)f)]).ConfigureAwait(false);
        return WriteOutcome.Committed;
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        var database = redis.GetDatabase();
        foreach (var endpoint in redis.GetEndPoints())
        {
            var server = redis.GetServer(endpoint);
            if (server.IsReplica)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(pattern: $"h:{cacheName},c:*"))
            {
                await database.KeyDeleteAsync(key).ConfigureAwait(false);
            }
        }
    }
}
