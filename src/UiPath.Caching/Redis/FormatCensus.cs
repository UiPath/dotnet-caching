using System.Globalization;
using System.Text;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

/// <summary>Counts the strings and hashes under a key pattern by the format their value is stored in, to tell when the values of an old format are gone.</summary>
internal sealed class FormatCensus(
    IRedisConnector redis,
    string keyPrefix,
    ValueSerializerRegistry registry,
    IRedisKeyStrategy strings,
    IRedisKeyStrategy hashes,
    CacheKeyCasing casing,
    Action? onPage = null)
{
    // A hint to the server of how many keys a SCAN page looks at, so one call stays short however many keys there are.
    internal const int ScanCount = 500;

    /// <summary>The most user fields of one hash that are looked at; a bigger hash is counted by its first ones.</summary>
    internal const int MaxHashFields = 256;

    /// <summary>The most commands of a page in flight at once, so a page of small values is not a burst of five hundred.</summary>
    internal const int MaxInFlight = 32;

    private const int HeadLength = 2;
    private const int HashPage = 100;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<IReadOnlyDictionary<string, long>> CountAsync(string keyPattern, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPattern);
        token.ThrowIfCancellationRequested();
        await redis.ConnectAsync(token).ConfigureAwait(false);
        var database = redis.Database;
        var prefix = await PrefixPatternAsync(database).WaitAsync(token).ConfigureAwait(false);
        // The keys a cache writes are its strategy's: another segment for a string than for a hash, and cased as the cache cases them.
        var name = new CacheKey(keyPattern, casing).Name;
        var stringPattern = prefix + Literally(strings.GetRedisKey(new CacheKey(keyPattern, casing)).ToString(), name);
        var hashPattern = prefix + Literally(hashes.GetRedisKey(new CacheKey(keyPattern, casing)).ToString(), name);
        var multiplexer = database.Multiplexer;
        var reader = multiplexer.GetDatabase(database.Database);
        var primaries = redis.GetPrimaries().ToList();
        if (primaries.Count == 0)
        {
            // A connector written outside this library inherits the empty default, yet its multiplexer knows every shard.
            primaries = [.. multiplexer.GetServers().Where(static server => !server.IsReplica && server.IsConnected)];
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var primary in primaries)
        {
            // One type at a time, so the server filters and no key costs a TYPE.
            await ScanAsync(primary, database.Database, reader, stringPattern, "string", counts, token).ConfigureAwait(false);
            await ScanAsync(primary, database.Database, reader, hashPattern, "hash", counts, token).ConfigureAwait(false);
        }

        return counts;
    }

    /// <summary>A value is read by its first bytes. Null for one that is not counted: an empty value is the cached-null marker, which carries no format.</summary>
    internal static string? Classify(ReadOnlySpan<byte> head, ValueSerializerRegistry registry)
    {
        if (head.IsEmpty)
        {
            return null;
        }

        if (!ValueHeader.TryRead(head, out var formatId, out _))
        {
            return CacheValueFormats.JsonName;
        }

        return formatId < 0 || !registry.TryGetByFormat(formatId, out _) ? CacheValueFormats.UnknownName : registry.NameOf(formatId);
    }

    // What the strategy puts around the name (the application, the separators, a shard tag) is literal, so its glob characters are escaped; the name is the pattern the caller gave and keeps its own.
    private static string Literally(string key, string name)
    {
        var at = key.LastIndexOf(name, StringComparison.Ordinal);
        return at < 0 ? Escape(key) : Escape(key[..at]) + name + Escape(key[(at + name.Length)..]);
    }

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '*' or '?' or '[' or ']' or '\\')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    // The connector's prefix as the server holds it, with the glob characters a pattern would read escaped; only a prefix that is not text cannot be looked for.
    private async Task<string> PrefixPatternAsync(IDatabase database)
    {
        var bytes = await ConnectorKeyPrefix.ResolveBytesAsync(database, keyPrefix).ConfigureAwait(false);
        try
        {
            return Escape(StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException("The connector's key prefix is not UTF-8 text, so keys under it cannot be looked for with a pattern.");
        }
    }

    private async Task ScanAsync(IServer primary, int database, IDatabase reader, string pattern, string type, Dictionary<string, long> counts, CancellationToken token)
    {
        ulong cursor = 0;
        do
        {
            token.ThrowIfCancellationRequested();
            var reply = await primary.ExecuteAsync(database, "SCAN", [cursor, "MATCH", pattern, "COUNT", ScanCount, "TYPE", type], CommandFlags.DemandMaster).WaitAsync(token).ConfigureAwait(false);
            cursor = ulong.Parse(reply[0].ToString()!, CultureInfo.InvariantCulture);
            var keys = ((RedisResult[]?)reply[1])?.Select(static key => (RedisKey)(byte[])key!).ToArray() ?? [];
            for (var start = 0; start < keys.Length; start += MaxInFlight)
            {
                var chunk = keys.AsMemory(start, Math.Min(MaxInFlight, keys.Length - start)).ToArray();
                var formats = await Task.WhenAll(chunk.Select(key => type == "string" ? StringFormatsAsync(reader, key) : HashFormatsAsync(reader, key, token))).WaitAsync(token).ConfigureAwait(false);
                foreach (var found in formats)
                {
                    foreach (var format in found)
                    {
                        counts[format] = counts.GetValueOrDefault(format) + 1;
                    }
                }
            }

            onPage?.Invoke();
        }
        while (cursor != 0);
    }

    private async Task<IReadOnlyCollection<string>> StringFormatsAsync(IDatabase reader, RedisKey key)
    {
        try
        {
            var head = (byte[]?)await reader.StringGetRangeAsync(key, 0, HeadLength - 1, CommandFlags.DemandMaster).ConfigureAwait(false);
            return head is not null && Classify(head, registry) is { } format ? [format] : [];
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("WRONGTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
    }

    // A hash is in every format one of its user fields is in, so a hash with fields in two formats is counted under both and "no json left" means exactly that; _version_ and _metadata_ are not values of the type.
    private async Task<IReadOnlyCollection<string>> HashFormatsAsync(IDatabase reader, RedisKey key, CancellationToken token)
    {
        HashSet<string> formats = [];
        try
        {
            var seen = 0;
            await foreach (var entry in reader.HashScanAsync(key, pageSize: HashPage, flags: CommandFlags.DemandMaster).WithCancellation(token).ConfigureAwait(false))
            {
                if (KnownFieldNames.IsSystemField(entry.Name))
                {
                    continue;
                }

                var value = (byte[]?)entry.Value;
                if (value is not null && Classify(value.AsSpan(0, Math.Min(value.Length, HeadLength)), registry) is { } format)
                {
                    formats.Add(format);
                }

                if (++seen >= MaxHashFields)
                {
                    break;
                }
            }
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("WRONGTYPE", StringComparison.OrdinalIgnoreCase))
        {
            // The key became another type since it was listed.
        }

        return formats;
    }
}
