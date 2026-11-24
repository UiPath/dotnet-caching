using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The helping cleanup of a live supersession marker: the delete, fenced by the marker's own binding, of the superseded in-place copy, then the removal of the exact marker. It publishes nothing.</summary>
internal sealed class SupersessionHelper(Func<KeyMoveData?> data, Func<string, string> inPlaceKey, Func<string, string> nativeKey, string group, string cacheId) : ISupersessionHandler
{
    public async Task<SupersessionAction> OnSupersededAsync(SupersededKey context, CancellationToken cancellationToken)
    {
        if (data() is not { } plane)
        {
            return SupersessionAction.Skip;
        }

        var l = inPlaceKey(context.Key);
        var n = nativeKey(context.Key);
        long deleted;
        try
        {
            deleted = await plane.Scripts.SourceDeleteGuardedAsync(l, plane.Names.ControlFor(group, l), context.Identity, MarkerBinding.BindingOf(context.Marker.ToString())).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            return SupersessionAction.Skip;
        }

        if (deleted is MoveScripts.IdentityMismatch or MoveScripts.BindingEnded)
        {
            return SupersessionAction.Rerun;
        }

        RouterTelemetry.CountMarker("superseded_helped", cacheId, group);
        try
        {
            _ = await plane.Scripts.UnmarkAsync(plane.Names.ControlFor(group, n), plane.Names.MarkerField(l), context.Marker.ToString()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            // L is gone, so a marker that stays is an orphan: the next write or delete of the key, or a sweep, removes it.
            _ = ex;
        }

        return SupersessionAction.Retry;
    }
}
