using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveClient<T>
{
    private static PathRead<T> Interpret(KeyMoveSnap snap, IKeyMoveCodec<T> codec)
    {
        if (snap.Kind != ReadOutcome.Found)
        {
            return PathRead<T>.Of(snap.Kind);
        }

        Decoded<T> decoded;
        try
        {
            decoded = codec.Decode(snap.Bytes ?? []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PathRead<T>.Of(ReadOutcome.Unreadable);
        }

        return decoded.Class switch
        {
            DecodeClass.Expired => PathRead<T>.Of(ReadOutcome.Absent),
            DecodeClass.Unreadable => PathRead<T>.Of(ReadOutcome.Unreadable),
            _ when decoded.Value is null => PathRead<T>.Of(ReadOutcome.Unreadable),
            _ => new PathRead<T>(ReadOutcome.Found, decoded.Value, snap.TtlMs < 0 ? null : snap.At.AddMilliseconds(snap.TtlMs)),
        };
    }

    private async ValueTask<KeyMoveSnap> SnapAsync(string key, bool replica, SchedulePointStep step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Data is not { } data || !data.IsConnected())
        {
            return KeyMoveSnap.Of(ReadOutcome.Unavailable);
        }

        await At(step).ConfigureAwait(false);
        try
        {
            var read = await data.Db.StringGetWithExpiryAsync(Key(key), replica ? CommandFlags.PreferReplica : Master).ConfigureAwait(false);
            if (read.Value.IsNull)
            {
                return KeyMoveSnap.Of(ReadOutcome.Absent);
            }

            var ttl = read.Expiry is { } left ? (long)Math.Ceiling(left.TotalMilliseconds) : -1;
            return new KeyMoveSnap(ReadOutcome.Found, (byte[]?)read.Value ?? [], ttl, _p.Clock.GetUtcNow());
        }
        catch (RedisServerException ex) when (ex.Message.Contains("WRONGTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return KeyMoveSnap.Of(ReadOutcome.Unreadable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return KeyMoveSnap.Of(ReadOutcome.Unavailable);
        }
    }

    private async ValueTask<KeyMoveSnap> SourceSnapAsync(string key, SchedulePointStep step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Source.IsAvailable)
        {
            return KeyMoveSnap.Of(ReadOutcome.Unavailable);
        }

        await At(step).ConfigureAwait(false);
        return await Source.SnapshotAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool?> SourceExistsAsync(string key, SchedulePointStep step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Source.IsAvailable)
        {
            return null;
        }

        await At(step).ConfigureAwait(false);
        return await Source.ExistsAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool?> ExistsAsync(string key, SchedulePointStep step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Data is not { } data || !data.IsConnected())
        {
            return null;
        }

        await At(step).ConfigureAwait(false);
        try
        {
            return await data.Db.KeyExistsAsync(Key(key), Master).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
