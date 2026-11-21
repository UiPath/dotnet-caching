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

    internal const string EmptyReason = "empty";
    internal const string OversizeReason = "oversize";
    internal const string NoTtlReason = "no_ttl";
    internal const string HashSampledReason = "hash_sampled";

    private const int HeadLength = 2;
    private const int HashPage = 100;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public Task<IReadOnlyDictionary<string, long>> CountAsync(string keyPattern, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPattern);
        return CountAsync(new CacheKey(keyPattern, casing), token);
    }

    public Task<IReadOnlyDictionary<string, long>> CountAsync(CacheKey keyPattern, CancellationToken token)
    {
        if (keyPattern.IsNull)
        {
            throw new ArgumentException("The key pattern must contain text.", nameof(keyPattern));
        }

        return CountCoreAsync(keyPattern, token);
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

    /// <summary>The census reason of a value stored in <paramref name="format"/>, one of <see cref="Classify"/>'s answers: <c>format_json</c>, <c>format_&lt;name&gt;</c> or <c>format_unknown</c>.</summary>
    internal static string FormatReason(string format) => "format_" + format;

    /// <summary>One SCAN call of a type on a primary: the cursor to continue with (zero when the primary is done) and the keys this page returned, which may repeat across pages.</summary>
    internal static async Task<(ulong Cursor, RedisKey[] Keys)> ScanPageAsync(IServer primary, int database, string pattern, string type, ulong cursor, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var reply = await primary.ExecuteAsync(database, "SCAN", [cursor, "MATCH", pattern, "COUNT", count, "TYPE", type], CommandFlags.DemandMaster).WaitAsync(token).ConfigureAwait(false);
        var next = ulong.Parse(reply[0].ToString(), CultureInfo.InvariantCulture);
        var keys = ((RedisResult[]?)reply[1])?.Select(static key => (RedisKey)((byte[])key)!).ToArray() ?? [];
        return (next, keys);
    }

    /// <summary>Every reason a census can give: the fixed ones, and a format reason for json, unknown and each registered format, so a reading that has gone to zero can still be told.</summary>
    internal IReadOnlyCollection<string> KnownReasons() =>
    [
        .. new[] { EmptyReason, OversizeReason, NoTtlReason, HashSampledReason, FormatReason(CacheValueFormats.JsonName), FormatReason(CacheValueFormats.UnknownName) }
            .Concat(registry.Entries.Select(static entry => FormatReason(entry.Name)))
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>The databases, patterns and primaries a pass over a key pattern looks at, the primaries in a fixed order so a cursor can name one.</summary>
    internal async Task<ScanPlan> PlanAsync(CacheKey keyPattern, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await redis.ConnectAsync(token).ConfigureAwait(false);
        var database = redis.Database;
        var prefixBytes = await ConnectorKeyPrefix.ResolveBytesAsync(database, keyPrefix).AsTask().WaitAsync(token).ConfigureAwait(false);
        var prefix = PrefixPattern(prefixBytes);
        // The keys a cache writes are its strategy's: another segment for a string than for a hash, and cased as the cache cases them.
        var name = keyPattern.Name;
        var stringPattern = prefix + Literally(strings.GetRedisKey(keyPattern).ToString(), name);
        var hashPattern = prefix + Literally(hashes.GetRedisKey(keyPattern).ToString(), name);
        var multiplexer = database.Multiplexer;
        var primaries = redis.GetPrimaries().ToList();
        if (primaries.Count == 0)
        {
            // A connector written outside this library inherits the empty default, yet its multiplexer knows every shard.
            primaries = [.. multiplexer.GetServers().Where(static server => !server.IsReplica && server.IsConnected)];
        }

        primaries.Sort(static (a, b) => string.CompareOrdinal(a.EndPoint?.ToString(), b.EndPoint?.ToString()));
        return new(database, multiplexer.GetDatabase(database.Database), prefixBytes, stringPattern, hashPattern, primaries);
    }

    /// <summary>
    /// As <see cref="CountAsync(CacheKey, CancellationToken)"/>, with why a value is where it is: its format, empty, over the size a rewrite carries, or without a TTL; a hash is
    /// sampled and said so. Each key costs a <c>PTTL</c> and, for a headerless string, a <c>STRLEN</c>, so this is a tool for an operator, not for a request.
    /// </summary>
    internal async Task<FormatCensusReport> CountByReasonAsync(CacheKey keyPattern, int maxValueBytes, CancellationToken token)
    {
        if (keyPattern.IsNull)
        {
            throw new ArgumentException("The key pattern must contain text.", nameof(keyPattern));
        }

        var plan = await PlanAsync(keyPattern, token).ConfigureAwait(false);
        var formats = new Dictionary<string, long>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, long>(StringComparer.Ordinal);
        long scanned = 0;
        foreach (var primary in plan.Primaries)
        {
            foreach (var (pattern, type) in new[] { (plan.StringPattern, "string"), (plan.HashPattern, "hash") })
            {
                ulong cursor = 0;
                do
                {
                    (cursor, var keys) = await ScanPageAsync(primary, plan.Database.Database, pattern, type, cursor, ScanCount, token).ConfigureAwait(false);
                    for (var start = 0; start < keys.Length; start += MaxInFlight)
                    {
                        var chunk = keys.AsMemory(start, Math.Min(MaxInFlight, keys.Length - start)).ToArray();
                        var found = await Task.WhenAll(chunk.Select(key => type == "string" ? StringReasonsAsync(plan.Reader, key, maxValueBytes) : HashReasonsAsync(plan.Reader, key, token))).WaitAsync(token).ConfigureAwait(false);
                        foreach (var key in found)
                        {
                            if (key.Gone)
                            {
                                continue;
                            }

                            scanned++;
                            foreach (var format in key.Formats)
                            {
                                formats[format] = formats.GetValueOrDefault(format) + 1;
                            }

                            foreach (var reason in key.Reasons)
                            {
                                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                            }
                        }
                    }

                    onPage?.Invoke();
                }
                while (cursor != 0);
            }
        }

        return new FormatCensusReport(formats, reasons, scanned);
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
    private static string PrefixPattern(byte[] bytes)
    {
        try
        {
            return Escape(StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException("The connector's key prefix is not UTF-8 text, so keys under it cannot be looked for with a pattern.");
        }
    }

    private async Task<IReadOnlyDictionary<string, long>> CountCoreAsync(CacheKey keyPattern, CancellationToken token)
    {
        var plan = await PlanAsync(keyPattern, token).ConfigureAwait(false);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var primary in plan.Primaries)
        {
            // One type at a time, so the server filters and no key costs a TYPE.
            await ScanAsync(primary, plan.Database.Database, plan.Reader, plan.StringPattern, "string", counts, token).ConfigureAwait(false);
            await ScanAsync(primary, plan.Database.Database, plan.Reader, plan.HashPattern, "hash", counts, token).ConfigureAwait(false);
        }

        return counts;
    }

    private async Task ScanAsync(IServer primary, int database, IDatabase reader, string pattern, string type, Dictionary<string, long> counts, CancellationToken token)
    {
        ulong cursor = 0;
        do
        {
            (cursor, var keys) = await ScanPageAsync(primary, database, pattern, type, cursor, ScanCount, token).ConfigureAwait(false);
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

    private async Task<KeyFinding> StringReasonsAsync(IDatabase reader, RedisKey key, int maxValueBytes)
    {
        try
        {
            var headTask = reader.StringGetRangeAsync(key, 0, HeadLength - 1, CommandFlags.DemandMaster);
            var ttlTask = reader.KeyTimeToLiveAsync(key, CommandFlags.DemandMaster);
            var head = (byte[]?)await headTask.ConfigureAwait(false);
            var ttl = await ttlTask.ConfigureAwait(false);
            List<string> formats = [];
            List<string> reasons = [];
            var format = head is null ? null : Classify(head, registry);
            if (format is null)
            {
                // An empty string is the cached-null marker; a key that went away since the scan answers empty too, so it is told by asking for it.
                if (!await reader.KeyExistsAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false))
                {
                    return KeyFinding.Missing;
                }

                reasons.Add(EmptyReason);
            }
            else
            {
                formats.Add(format);
                reasons.Add(FormatReason(format));
                if (format == CacheValueFormats.JsonName && await reader.StringLengthAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false) > maxValueBytes)
                {
                    reasons.Add(OversizeReason);
                }
            }

            // A key that is there and answers no lifetime has none.
            if (ttl is null)
            {
                reasons.Add(NoTtlReason);
            }

            return new(formats, reasons, Gone: false);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("WRONGTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return KeyFinding.Missing;
        }
    }

    private async Task<KeyFinding> HashReasonsAsync(IDatabase reader, RedisKey key, CancellationToken token) =>
        new(await HashFormatsAsync(reader, key, token).ConfigureAwait(false), [HashSampledReason], Gone: false);

    private readonly record struct KeyFinding(IReadOnlyCollection<string> Formats, IReadOnlyCollection<string> Reasons, bool Gone)
    {
        public static KeyFinding Missing => new([], [], Gone: true);
    }

    /// <summary>What a pass over a key pattern looks at.</summary>
    internal sealed record ScanPlan(IDatabase Database, IDatabase Reader, byte[] PrefixBytes, string StringPattern, string HashPattern, IReadOnlyList<IServer> Primaries);
}
