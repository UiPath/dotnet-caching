namespace UiPath.Caching.KeyMove;

internal sealed class StageCommands(StateStore store, MoveScripts scripts, MoveKeyNames names, StageDriverOptions options)
{
    public Task<InitResult> InitAsync(string group, IEnumerable<string> cacheIds, CancellationToken cancellationToken) => store.InitAsync(group, cacheIds, cancellationToken);

    public Task<bool> HoldAsync(string group, string reason) => store.SetHoldAsync(group, HoldKind.Operator, reason);

    /// <summary>Clears both hold classes.</summary>
    public Task<bool> ResumeAsync(string group) => store.ClearHoldAsync(group, HoldKind.Operator, HoldKind.Automatic);

    /// <summary>Stores a consent for the state as it is now; it is void once the state moves or the inventory it covers changes.</summary>
    public async Task<ApproveResult> ApproveAsync(string group, string kind, string confirm, string operatorId, CancellationToken cancellationToken)
    {
        if (!ApprovalKinds.All.Contains(kind))
        {
            return ApproveResult.UnknownKind;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(confirm);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
        if (kind == ApprovalKinds.StartUnsettled && confirm != options.UpgradeHoldOverrideText)
        {
            throw new ArgumentException($"The override of the unchanged-view hold needs exactly this text: \"{options.UpgradeHoldOverrideText}\".", nameof(confirm));
        }

        if (await store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is not { } state)
        {
            return ApproveResult.NoState;
        }

        if (!Applies(kind, state))
        {
            return ApproveResult.NotApplicable;
        }

        var approval = new Approval(kind, state.Identity, Approval.EvidenceOf(kind, state), operatorId, confirm);
        var code = await scripts.FieldsAsync(names.State(group), state.Id, [(StateFields.ApprovalPrefix + kind, approval.Encode())], []).ConfigureAwait(false);
        return code switch
        {
            MoveScripts.Written => ApproveResult.Stored,
            MoveScripts.IdentityMismatch => ApproveResult.StateMoved,
            _ => ApproveResult.NoState,
        };
    }

    /// <summary>Lets the forward transition stuck at <c>dur=0</c> go on past a barrier that cannot confirm, with <c>wait_skipped</c>.</summary>
    public Task<ApproveResult> SkipBarrierAsync(string group, string confirm, string operatorId, CancellationToken cancellationToken) =>
        ApproveAsync(group, ApprovalKinds.SkipBarrier, confirm, operatorId, cancellationToken);

    /// <summary>X11 for a closed group: the leader flips to S0 and purges.</summary>
    public Task<ApproveResult> AbandonAsync(string group, string confirm, string operatorId, CancellationToken cancellationToken) =>
        ApproveAsync(group, ApprovalKinds.Abandon, confirm, operatorId, cancellationToken);

    private static bool Applies(string kind, StateRecord state) => kind switch
    {
        ApprovalKinds.Start or ApprovalKinds.StartUnsettled or ApprovalKinds.StartNoDryRun or ApprovalKinds.StartMemory or ApprovalKinds.ControlRecordsEvictable => state is { State: "S0", Durable: true },
        ApprovalKinds.Close or ApprovalKinds.CloseForce => state is { State: "S4", Direction: "fwd" },
        ApprovalKinds.Release => state is { State: "S4", Direction: "rev" },
        ApprovalKinds.ForceReverse => state is { State: "S3" or "S4", Direction: "fwd" },
        ApprovalKinds.SkipBarrier => !state.Durable,
        ApprovalKinds.Abandon or ApprovalKinds.SkipLsweptWait => state is { State: "S5" },
        _ => false,
    };
}
