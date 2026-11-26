using System.Globalization;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed class KeyMoveOperations(Router router, KeyMoveSetup setup) : IKeyMoveOperations
{
    private static readonly char[] ReasonSeparator = ['\n'];

    public async Task<KeyMoveStatus?> GetStatusAsync(string group, CancellationToken cancellationToken = default)
    {
        var state = await Runtime().Store.ReadAsync(Registered(group), cancellationToken).ConfigureAwait(false);
        return state is null ? null : StatusOf(group, state);
    }

    public async Task<bool> InitializeAsync(string group, CancellationToken cancellationToken = default)
    {
        var name = Registered(group);
        var cacheIds = setup.Caches.Where(c => c.Group == name).Select(c => c.Name);
        var result = await Runtime().Commands.InitAsync(name, cacheIds, cancellationToken).ConfigureAwait(false);
        return result.Code != MoveScripts.StateNotInitial;
    }

    public Task<bool> HoldAsync(string group, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return Runtime().Commands.HoldAsync(Registered(group), reason).WaitAsync(cancellationToken);
    }

    public Task<bool> ResumeAsync(string group, CancellationToken cancellationToken = default) =>
        Runtime().Commands.ResumeAsync(Registered(group)).WaitAsync(cancellationToken);

    public async Task<KeyMoveApproval> ApproveAsync(string group, string kind, string confirm, string operatorId, CancellationToken cancellationToken = default)
    {
        var result = await Runtime().Commands.ApproveAsync(Registered(group), kind, confirm, operatorId, cancellationToken).ConfigureAwait(false);
        return (KeyMoveApproval)(int)result;
    }

    public async Task<KeyMoveDryRun> RunDryRunAsync(string group, bool reverse = false, CancellationToken cancellationToken = default)
    {
        var report = await router.DryRunAsync(Registered(group), reverse ? "rev" : "fwd", cancellationToken).ConfigureAwait(false);
        var caches = report.Caches.ToDictionary(c => c.Key, c => new KeyMoveDryRunCache(c.Value.Counts, c.Value.Sizes), StringComparer.Ordinal);
        return new KeyMoveDryRun(report.Scanned, report.EstimatedNativeBytes, report.EstimatedDuration, caches);
    }

    internal static KeyMoveStatus StatusOf(string group, StateRecord state)
    {
        var approvals = (state.FieldNames ?? [])
            .Where(f => f.StartsWith(StateFields.ApprovalPrefix, StringComparison.Ordinal))
            .Select(f => f[StateFields.ApprovalPrefix.Length..])
            .Where(kind => Approval.ValidFor(state, kind) is not null)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var blocked = state.Field(StateFields.Blocked);
        var since = long.TryParse(state.Field(StateFields.BlockedAt), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && blocked.Length > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : (DateTimeOffset?)null;
        return new KeyMoveStatus(
            group,
            StageOf(state.State),
            state.Direction == "rev",
            state.Durable,
            NullIfEmpty(state.HoldOp),
            NullIfEmpty(state.HoldAuto),
            approvals,
            state.Residue ?? new Dictionary<string, long>(),
            blocked.Split(ReasonSeparator, StringSplitOptions.RemoveEmptyEntries),
            since);
    }

    internal static KeyMoveStage StageOf(string state) => state switch
    {
        KeyMoveStates.Ready => KeyMoveStage.Ready,
        KeyMoveStates.Barrier => KeyMoveStage.Barrier,
        KeyMoveStates.Moving => KeyMoveStage.Moving,
        KeyMoveStates.Drained => KeyMoveStage.Drained,
        KeyMoveStates.Closed => KeyMoveStage.Closed,
        _ => KeyMoveStage.NotStarted,
    };

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private KeyMoveRuntime Runtime() =>
        router.KeyMove?.Runtime ?? throw new InvalidOperationException("The library is not started, or it stopped: the key move operations need its Redis connection.");

    private string Registered(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return setup.Groups.Any(g => g.Name == group)
            ? group
            : throw new ArgumentException($"The group {group} is not a key move group of this application.", nameof(group));
    }
}
