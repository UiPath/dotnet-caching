using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveClient<T>
{
    private static PathRead<T> Interpret(KeyMoveSnap snap, IKeyMoveCodec<T> codec) => KeyPromotion<T>.Interpret(snap, codec);

    private ValueTask<KeyMoveSnap> SnapAsync(string key, bool replica, SchedulePointStep step, CancellationToken cancellationToken) =>
        _promotion.SnapAsync(key, replica, step, cancellationToken);

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

    private ValueTask<bool?> ExistsAsync(string key, SchedulePointStep step, CancellationToken cancellationToken) =>
        _promotion.ExistsAsync(key, step, cancellationToken);
}
