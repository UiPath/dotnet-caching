using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The reads of a hash cache's two sides that the key move client and the promotion share: the snapshot of one primary, the existence probe, and the union read.</summary>
internal sealed class HashReads<T>(KeyMoveHashClientParts<T> parts, string[]? fields = null)
    where T : class
{
    private const CommandFlags Master = CommandFlags.DemandMaster;

    private KeyMoveData? Data => parts.Plane.Data;

    /// <summary>A hash that is cached empty on the native side is the answer when that side caches nulls; on the in-place side it is a miss. A read never applies the caps of a promotion.</summary>
    public static HashRead<T> Interpret(HashSnap snap, IKeyMoveHashCodec<T> codec, bool authoritativeNull = false)
    {
        if (snap.Kind != ReadOutcome.Found)
        {
            return HashRead<T>.Of(snap.Kind);
        }

        DecodedHash<T> decoded;
        try
        {
            decoded = codec.DecodeFields(snap.Stored(), capped: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return HashRead<T>.Of(ReadOutcome.Unreadable);
        }

        return decoded.Class switch
        {
            DecodeClass.Null when authoritativeNull && codec.CachesNulls => new HashRead<T>(ReadOutcome.Absent, null, null) { Stops = true },
            DecodeClass.Expired or DecodeClass.Null => HashRead<T>.Of(ReadOutcome.Absent),
            DecodeClass.Unreadable => HashRead<T>.Of(ReadOutcome.Unreadable),
            _ => new HashRead<T>(ReadOutcome.Found, new Dictionary<string, T?>(decoded.Fields, StringComparer.Ordinal), snap.TtlMs < 0 ? null : snap.At.AddMilliseconds(snap.TtlMs), decoded.Metadata, decoded.Version),
        };
    }

    public HashRead<T> InterpretRead(HashSnap snap, IKeyMoveHashCodec<T> codec, bool authoritativeNull = false)
    {
        var read = Interpret(snap, codec, authoritativeNull);
        return read.Kind == ReadOutcome.Unreadable ? RecoverFields(snap, codec) : read;
    }

    public HashRead<T> RecoverFields(HashSnap snap, IKeyMoveHashCodec<T> codec)
    {
        if (fields is null || snap.Entries is null)
        {
            return HashRead<T>.Of(ReadOutcome.Unreadable);
        }

        // Recover from the same snapshot so a decode failure cannot switch sides or mix versions.
        var selected = new HashSet<string>(fields, StringComparer.Ordinal);
        var recovered = new Dictionary<string, T?>(StringComparer.Ordinal);
        foreach (var entry in snap.Entries)
        {
            var field = entry.Name.ToString();
            if (!selected.Contains(field))
            {
                continue;
            }

            var read = Interpret(snap with { Entries = [entry] }, codec);
            if (read.Fields is { } decoded && decoded.TryGetValue(field, out var value))
            {
                recovered.Add(field, value);
            }
        }

        if (recovered.Count == 0)
        {
            return HashRead<T>.Of(ReadOutcome.Unreadable);
        }

        return new HashRead<T>(ReadOutcome.Found, recovered, snap.TtlMs < 0 ? null : snap.At.AddMilliseconds(snap.TtlMs), Uncached: true);
    }

    public ValueTask At(SchedulePointStep step) => parts.Schedule.AtAsync(step);

    /// <summary>The fields and the remaining life of a hash, together: one transaction on a primary, or two commands on a replica.</summary>
    /// <param name="cap">When set, the field count is asked first and a hash above it is reported without being read.</param>
    public async ValueTask<HashSnap> SnapAsync(string key, bool replica, SchedulePointStep step, CancellationToken cancellationToken, int? cap = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Data is not { } data || !data.IsConnected())
        {
            return HashSnap.Of(ReadOutcome.Unavailable);
        }

        await At(step).ConfigureAwait(false);
        try
        {
            if (cap is { } most && await CheckCapAsync(data, key, most).ConfigureAwait(false) is { } capped)
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

            return new HashSnap(ReadOutcome.Found, entries, left is { } span ? (long)Math.Ceiling(span.TotalMilliseconds) : -1, parts.Clock.GetUtcNow(), entries.Length);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("WRONGTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return HashSnap.Of(ReadOutcome.Unreadable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HashSnap.Of(ReadOutcome.Unavailable);
        }
    }

    public async ValueTask<bool?> ExistsAsync(string key, SchedulePointStep step, CancellationToken cancellationToken)
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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>N on a replica, N on the master, then L and N on the master again: a replica miss is never a miss before the primary agrees, and forward N is the destination.</summary>
    public async ValueTask<HashReadStep<T>> ReadUnionAsync(string key, bool withSource, CancellationToken cancellationToken)
    {
        var n = parts.NativeKey(key);
        var first = InterpretRead(await SnapAsync(n, replica: true, SchedulePointStep.ReadDestination, cancellationToken).ConfigureAwait(false), parts.Native, authoritativeNull: true);
        if (!first.FallsThrough)
        {
            return HashReadStep<T>.Of(first);
        }

        var master = InterpretRead(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), parts.Native, authoritativeNull: true);
        if (!master.FallsThrough || !withSource)
        {
            return HashReadStep<T>.Of(master);
        }

        var source = InterpretRead(await SnapAsync(parts.InPlaceKey(key), replica: false, SchedulePointStep.ReadSource, cancellationToken).ConfigureAwait(false), parts.InPlace);
        if (source.Kind != ReadOutcome.Absent)
        {
            return HashReadStep<T>.Of(source);
        }

        return HashReadStep<T>.Of(InterpretRead(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), parts.Native, authoritativeNull: true));
    }

    // A hash with no field is absent, and one above the cap is reported without being read; null when the hash is to be read.
    private static async ValueTask<HashSnap?> CheckCapAsync(KeyMoveData data, string key, int most)
    {
        var length = await data.Db.HashLengthAsync(key, Master).ConfigureAwait(false);
        if (length == 0)
        {
            return HashSnap.Of(ReadOutcome.Absent);
        }

        return length > most ? new HashSnap(ReadOutcome.Found, null, -1, default, length, TooLarge: true) : null;
    }
}
