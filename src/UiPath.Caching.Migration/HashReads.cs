using System.Text;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The reads of a hash cache's two sides that the key move client and the promotion share: the snapshot of one primary, the existence probe, and the union read.</summary>
internal sealed class HashReads<T>(KeyMoveHashClientParts<T> parts)
    where T : class
{
    private const CommandFlags Master = CommandFlags.DemandMaster;

    private KeyMoveData? Data => parts.Plane.Data;

    /// <summary>A hash that is cached empty on the native side is the answer when that side caches nulls; on the in-place side it is a miss. A read never applies the caps of a promotion.</summary>
    public static HashRead<T> Interpret(HashSnap snap, IKeyMoveHashCodec<T> codec, bool authoritativeNull = false, string[]? fields = null)
    {
        if (snap.Kind != ReadOutcome.Found)
        {
            return HashRead<T>.Of(snap.Kind);
        }

        var decoded = Decode(snap, codec);
        return decoded.Class switch
        {
            DecodeClass.Null when authoritativeNull && codec.CachesNulls => new HashRead<T>(ReadOutcome.Absent, null, null) { Stops = true },
            DecodeClass.Expired or DecodeClass.Null => HashRead<T>.Of(ReadOutcome.Absent),
            DecodeClass.Unreadable => RecoverFields(snap, codec, fields),
            _ => new HashRead<T>(ReadOutcome.Found, new Dictionary<string, T?>(decoded.Fields, StringComparer.Ordinal), Expiration(snap), decoded.Metadata, decoded.Version),
        };
    }

    public static HashRead<T> RecoverFields(HashSnap snap, IKeyMoveHashCodec<T> codec, string[]? fields)
    {
        if (fields is null || snap.Entries is null)
        {
            return HashRead<T>.Of(ReadOutcome.Unreadable);
        }

        // Use the field names' bytes before the codec decodes names, since an invalid name can decode to a valid one.
        var selected = new HashSet<RedisValue>(fields.Select(static field => (RedisValue)Encoding.UTF8.GetBytes(field)));
        var recovered = new Dictionary<string, T?>(StringComparer.Ordinal);
        IDictionary<string, string?>? metadata = null;
        long version = 0;
        var selectedFieldExists = false;
        foreach (var entry in snap.Entries)
        {
            if (KnownFieldNames.IsSystemField(entry.Name))
            {
                var system = Decode(snap with { Entries = [entry] }, codec);
                metadata ??= system.Metadata;
                version = system.Version != 0 ? system.Version : version;
                continue;
            }

            if (!selected.Contains((RedisValue)(ReadOnlyMemory<byte>)entry.Name))
            {
                continue;
            }

            selectedFieldExists = true;
            var decoded = Decode(snap with { Entries = [entry] }, codec);
            var name = entry.Name.ToString();
            if (decoded.Fields.TryGetValue(name, out var value))
            {
                recovered.TryAdd(name, value);
            }
        }

        return selectedFieldExists && recovered.Count == 0
            ? HashRead<T>.Of(ReadOutcome.Unreadable)
            : new HashRead<T>(ReadOutcome.Found, recovered, Expiration(snap), metadata, version, Uncached: true);
    }

    public ValueTask At(SchedulePointStep step) => parts.Schedule.AtAsync(step);

    /// <summary>The fields and the remaining life of a hash, together: one transaction on a primary, or two commands on a replica.</summary>
    /// <param name="cap">When set, the field count is asked first and a hash above it is reported without being read.</param>
    public async ValueTask<HashSnap> SnapAsync(string key, bool replica, SchedulePointStep step, CancellationToken cancellationToken, int? cap = null, bool propagateFailures = false, bool measureCappedTtl = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Data is not { } data || !data.IsConnected())
        {
            return HashSnap.Of(ReadOutcome.Unavailable);
        }

        await At(step).ConfigureAwait(false);
        try
        {
            if (cap is { } most && await CheckCapAsync(data, key, most, measureCappedTtl).ConfigureAwait(false) is { } capped)
            {
                return capped;
            }

            HashEntry[] entries;
            TimeSpan? left;
            if (replica)
            {
                var all = data.Db.HashGetAllAsync(key, CommandFlags.PreferReplica);
                var life = data.Db.KeyTimeToLiveAsync(key, CommandFlags.PreferReplica);
                entries = await all.ConfigureAwait(false);
                left = await life.ConfigureAwait(false);
            }
            else
            {
                var transaction = data.Db.CreateTransaction();
                var all = transaction.HashGetAllAsync(key, Master);
                var life = transaction.KeyTimeToLiveAsync(key, Master);
                if (!await transaction.ExecuteAsync(Master).ConfigureAwait(false))
                {
                    return HashSnap.Of(ReadOutcome.Unavailable);
                }

                entries = await all.ConfigureAwait(false);
                left = await life.ConfigureAwait(false);
            }

            if (entries.Length == 0)
            {
                return HashSnap.Of(ReadOutcome.Absent);
            }

            return new HashSnap(ReadOutcome.Found, entries, TtlMilliseconds(left), parts.Clock.GetUtcNow(), entries.Length);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("WRONGTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return HashSnap.Of(ReadOutcome.Unreadable);
        }
        catch (Exception ex) when (!propagateFailures && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            return HashSnap.Of(ReadOutcome.Unavailable);
        }
    }

    public async ValueTask<bool?> ExistsAsync(string key, SchedulePointStep step, CancellationToken cancellationToken, bool propagateFailures = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Data is not { } data || !data.IsConnected())
        {
            return null;
        }

        await At(step).ConfigureAwait(false);
        try
        {
            return await data.Db.KeyExistsAsync(key, Master).ConfigureAwait(false);
        }
        catch (Exception ex) when (!propagateFailures && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>N on a replica, N on the master, then L and N on the master again: a replica miss is never a miss before the primary agrees, and forward N is the destination.</summary>
    public async ValueTask<HashReadStep<T>> ReadUnionAsync(string key, bool withSource, string[]? fields, CancellationToken cancellationToken)
    {
        var n = parts.NativeKey(key);
        var first = Interpret(await SnapAsync(n, replica: true, SchedulePointStep.ReadDestination, cancellationToken).ConfigureAwait(false), parts.Native, authoritativeNull: true, fields: fields);
        if (!first.FallsThrough)
        {
            return HashReadStep<T>.Of(first);
        }

        var master = Interpret(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), parts.Native, authoritativeNull: true, fields: fields);
        if (!master.FallsThrough || !withSource)
        {
            return HashReadStep<T>.Of(master);
        }

        var source = Interpret(await SnapAsync(parts.InPlaceKey(key), replica: false, SchedulePointStep.ReadSource, cancellationToken).ConfigureAwait(false), parts.InPlace, fields: fields);
        if (source.Kind != ReadOutcome.Absent)
        {
            return HashReadStep<T>.Of(source);
        }

        return HashReadStep<T>.Of(Interpret(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), parts.Native, authoritativeNull: true, fields: fields));
    }

    private static DateTimeOffset? Expiration(HashSnap snap) => snap.TtlMs < 0 ? null : snap.At.AddMilliseconds(snap.TtlMs);

    private static long TtlMilliseconds(TimeSpan? life) => life is { } span ? (long)Math.Ceiling(span.TotalMilliseconds) : -1;

    private static DecodedHash<T> Decode(HashSnap snap, IKeyMoveHashCodec<T> codec)
    {
        try
        {
            return codec.DecodeFields(snap.Stored(), capped: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DecodedHash<T>.Of(DecodeClass.Unreadable, snap.Entries?.Length ?? 0);
        }
    }

    // A hash with no field is absent, and one above the cap is reported without being read; null when the hash is to be read.
    private static async ValueTask<HashSnap?> CheckCapAsync(KeyMoveData data, string key, int most, bool measureTtl)
    {
        var length = await data.Db.HashLengthAsync(key, Master).ConfigureAwait(false);
        if (length == 0)
        {
            return HashSnap.Of(ReadOutcome.Absent);
        }

        if (length <= most)
        {
            return null;
        }

        var ttl = measureTtl ? TtlMilliseconds(await data.Db.KeyTimeToLiveAsync(key, Master).ConfigureAwait(false)) : -1;
        return new HashSnap(ReadOutcome.Found, null, ttl, default, length, TooLarge: true);
    }
}
