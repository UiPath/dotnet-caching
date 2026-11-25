using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>Runs the dry run of a group on request and stores its report beside the group's state.</summary>
internal sealed class DryRunService(KeyMoveSetup setup, KeyMoveNode node, Func<string, MoveSide, DryRunner> createRunner, TimeProvider time)
{
    /// <param name="direction"><c>fwd</c> reads the in-place keys and stores the dry run the start gate accepts; <c>rev</c> counts the native keys by ownership and stores no stamp.</param>
    public async Task<DryRunReport> RunAsync(string group, string direction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!setup.Groups.Any(g => g.Name == group))
        {
            throw new ArgumentException($"'{group}' is not a key move group of this router.", nameof(group));
        }

        if (direction is not ("fwd" or "rev"))
        {
            throw new ArgumentException($"'{direction}' is not a direction: use fwd or rev.", nameof(direction));
        }

        var store = node.Runtime?.Store ?? throw new InvalidOperationException("The key move node is not running.");
        var id = (await store.ReadAsync(group, cancellationToken).ConfigureAwait(false))?.Id
            ?? throw new InvalidOperationException("The group has no key move state: Init it first.");
        var forward = direction == "fwd";
        var report = await createRunner(group, forward ? MoveSide.Legacy : MoveSide.Native).RunAsync(cancellationToken).ConfigureAwait(false);
        var stamp = forward && !report.HasUnavailableKeys ? new DryRunStamp(time.GetUtcNow(), report.EstimatedNativeBytes, report.Summary()) : null;
        cancellationToken.ThrowIfCancellationRequested();
        if (!await store.StoreDryRunAsync(group, direction, id, stamp, report.Fields(), setup.Driver.DryRunMaxAge).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The group's key move state changed during the dry run.");
        }

        return report;
    }
}
